using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json;

namespace AIClockBridge;

// 天氣頁。橋接程式向 Open-Meteo（免 API key、涵蓋台灣）取資料，
// 自己把「整個 240x240 畫面」畫好（繁體中文字、天氣圖示、三日預報），
// 轉成大端 RGB565 後由 /weather/screen.raw 提供；韌體只負責一列一列畫到螢幕上
// （做法和音樂頁封面一樣），所以韌體不需要任何字型。
//   https://api.open-meteo.com/v1/forecast?latitude=..&longitude=..&current=..&daily=..
// 每 15 分鐘更新一次；換城市時立即更新。
sealed class WeatherMonitor
{
    /// 台灣 22 縣市（座標取縣市政府所在地附近，天氣預報已足夠準確）。
    public static readonly (string Name, double Lat, double Lon)[] Cities =
    {
        ("台北市", 25.0375, 121.5637), ("新北市", 25.0120, 121.4650), ("基隆市", 25.1276, 121.7392),
        ("桃園市", 24.9937, 121.3010), ("新竹市", 24.8138, 120.9675), ("新竹縣", 24.8387, 121.0177),
        ("苗栗縣", 24.5602, 120.8214), ("台中市", 24.1477, 120.6736), ("彰化縣", 24.0518, 120.5161),
        ("南投縣", 23.9157, 120.6869), ("雲林縣", 23.7092, 120.4313), ("嘉義市", 23.4801, 120.4491),
        ("嘉義縣", 23.4518, 120.2555), ("台南市", 22.9999, 120.2270), ("高雄市", 22.6273, 120.3014),
        ("屏東縣", 22.5519, 120.5487), ("宜蘭縣", 24.7021, 121.7378), ("花蓮縣", 23.9871, 121.6015),
        ("台東縣", 22.7583, 121.1444), ("澎湖縣", 23.5711, 119.5793), ("金門縣", 24.4493, 118.3767),
        ("連江縣", 26.1608, 119.9515),
    };

    const string CityKey = "weather_city";
    const string DefaultCity = "高雄市";
    const string FontName = "Microsoft JhengHei UI";
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static string City
    {
        get
        {
            var c = Settings.Get(CityKey);
            return Cities.Any(x => x.Name == c) ? c : DefaultCity;
        }
        set => Settings.Set(CityKey, value);
    }

    enum Kind { Clear, Partly, Cloudy, Fog, Drizzle, Rain, Thunder, Snow }

    sealed record Day(string Label, int Code, double Hi, double Lo, double Pop);

    sealed record Now(string City, double Temp, double Feels, double Humidity, double Wind,
                      int Code, bool IsDay, DateTime At, Day[] Days);

    readonly object _lock = new();
    byte[] _screen = Array.Empty<byte>();
    int _rev;
    double _temp;
    bool _busy;
    bool _again;
    System.Threading.Timer _timer;

    public void Start()
    {
        _timer = new System.Threading.Timer(async _ => await Refresh(), null, TimeSpan.Zero, RefreshEvery);
    }

    /// 立即更新；更新途中又被要求更新（例如連續換城市）會在這一輪結束後再跑一次。
    public async Task Refresh()
    {
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        try
        {
            do
            {
                _again = false;
                await Fetch();
            } while (_again);
        }
        finally
        {
            _busy = false;
        }
    }

    async Task Fetch()
    {
        try
        {
            var city = City;
            var c = Cities.First(x => x.Name == city);
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                "&timezone=Asia%2FTaipei&forecast_days=3", c.Lat, c.Lon);
            var json = await Http.GetStringAsync(url);
            var now = Parse(json, city);
            var bytes = Render(now);
            lock (_lock)
            {
                if (!bytes.AsSpan().SequenceEqual(_screen))
                {
                    _screen = bytes;
                    _rev++;
                }
                _temp = now.Temp;
            }
        }
        catch
        {
            // 網路暫時不通或格式變動：保留上一張畫面
        }
    }

    // ---------- 對外介面 ----------

    /// 裝置輪詢用的小 JSON：ready + rev（畫面內容有變才會加一）。
    public byte[] ToJson()
    {
        lock (_lock)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["ready"] = _screen.Length > 0,
                ["rev"] = _rev,
                ["temp"] = _temp,
            });
        }
    }

    /// 240x240 大端 RGB565（115200 bytes）；還沒取到資料時是空陣列。
    public byte[] ScreenRgb565
    {
        get
        {
            lock (_lock) return _screen;
        }
    }

    // ---------- 解析 ----------

    static Now Parse(string json, string city)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cur = root.GetProperty("current");
        var daily = root.GetProperty("daily");

        static double N(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
        static double At(JsonElement arr, int i) =>
            i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : double.NaN;

        var temp = N(cur, "temperature_2m");
        if (double.IsNaN(temp)) throw new FormatException("no temperature");

        var labels = new[] { "今天", "明天", "後天" };
        var codes = daily.GetProperty("weather_code");
        var his = daily.GetProperty("temperature_2m_max");
        var los = daily.GetProperty("temperature_2m_min");
        var pops = daily.GetProperty("precipitation_probability_max");
        var days = new Day[3];
        for (var i = 0; i < 3; i++)
        {
            var code = At(codes, i);
            days[i] = new Day(labels[i], double.IsNaN(code) ? 0 : (int)code, At(his, i), At(los, i), At(pops, i));
        }
        var curCode = N(cur, "weather_code");
        return new Now(city, temp, N(cur, "apparent_temperature"), N(cur, "relative_humidity_2m"),
            N(cur, "wind_speed_10m"), double.IsNaN(curCode) ? 0 : (int)curCode,
            N(cur, "is_day") >= 0.5, DateTime.Now, days);
    }

    // WMO weather code -> 圖示種類 / 中文說明
    static Kind KindOf(int c) => c switch
    {
        0 or 1 => Kind.Clear,
        2 => Kind.Partly,
        3 => Kind.Cloudy,
        45 or 48 => Kind.Fog,
        51 or 53 or 55 or 56 or 57 => Kind.Drizzle,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => Kind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => Kind.Snow,
        95 or 96 or 99 => Kind.Thunder,
        _ => Kind.Cloudy,
    };

    static string TextOf(int c) => c switch
    {
        0 => "晴朗",
        1 => "大致晴朗",
        2 => "多雲",
        3 => "陰天",
        45 or 48 => "起霧",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "凍毛毛雨",
        61 => "小雨",
        63 => "下雨",
        65 => "大雨",
        66 or 67 => "凍雨",
        71 => "小雪",
        73 => "下雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "陣雨",
        81 => "大陣雨",
        82 => "強陣雨",
        85 or 86 => "陣雪",
        95 => "雷雨",
        96 or 99 => "雷雨冰雹",
        _ => "多雲",
    };

    static string Deg(double v) => double.IsNaN(v) ? "--°" : $"{(int)Math.Round(v)}°";

    static string Pct(double v) => double.IsNaN(v) ? "--%" : $"{(int)Math.Round(v)}%";

    static (Color Top, Color Bottom) Palette(Kind kind, bool day)
    {
        if (!day && (kind == Kind.Clear || kind == Kind.Partly))
            return (Color.FromArgb(16, 26, 62), Color.FromArgb(6, 10, 28));
        return kind switch
        {
            Kind.Clear => (Color.FromArgb(46, 128, 210), Color.FromArgb(20, 60, 122)),
            Kind.Partly => (Color.FromArgb(70, 128, 192), Color.FromArgb(30, 64, 110)),
            Kind.Cloudy => (Color.FromArgb(104, 118, 138), Color.FromArgb(50, 60, 78)),
            Kind.Fog => (Color.FromArgb(112, 120, 130), Color.FromArgb(64, 70, 80)),
            Kind.Drizzle or Kind.Rain => (Color.FromArgb(64, 82, 106), Color.FromArgb(28, 38, 56)),
            Kind.Thunder => (Color.FromArgb(60, 54, 88), Color.FromArgb(24, 22, 42)),
            Kind.Snow => (Color.FromArgb(112, 134, 156), Color.FromArgb(62, 78, 98)),
            _ => (Color.FromArgb(70, 90, 120), Color.FromArgb(30, 40, 60)),
        };
    }

    // ---------- 繪圖 ----------

    static byte[] Render(Now w)
    {
        var kind = KindOf(w.Code);
        using var bmp = new Bitmap(240, 240, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var (top, bottom) = Palette(kind, w.IsDay);
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 240, 240), top, bottom, 90f))
                g.FillRectangle(bg, 0, 0, 240, 240);

            var white = Brushes.White;
            using var soft = new SolidBrush(Color.FromArgb(214, 224, 238));
            using var fCity = new Font(FontName, 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fSmall = new Font(FontName, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fTemp = new Font("Segoe UI", 56f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fCond = new Font(FontName, 20f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fHiLo = new Font(FontName, 14f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fInfo = new Font(FontName, 15f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fDay = new Font(FontName, 13f, FontStyle.Regular, GraphicsUnit.Pixel);

            // 城市名 + 更新時間
            g.DrawString(w.City, fCity, white, 14, 9);
            using (var far = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString($"{w.At:HH:mm} 更新", fSmall, soft, new RectangleF(110, 13, 118, 16), far);

            // 大圖示 + 大溫度 + 天氣說明 + 今日高低溫
            DrawIcon(g, kind, w.IsDay, new RectangleF(10, 36, 100, 100));
            g.DrawString(Deg(w.Temp), fTemp, white, 114, 24, StringFormat.GenericTypographic);
            g.DrawString(TextOf(w.Code), fCond, white, 118, 98);
            var today = w.Days[0];
            g.DrawString($"高 {Deg(today.Hi)}  低 {Deg(today.Lo)}", fHiLo, soft, 118, 126);

            // 分隔線
            using (var line = new Pen(Color.FromArgb(60, 255, 255, 255), 1))
                g.DrawLine(line, 14, 150, 226, 150);

            // 體感 / 濕度 / 降雨機率 / 風速
            g.DrawString($"體感 {Deg(w.Feels)}", fInfo, white, 14, 158);
            g.DrawString($"濕度 {Pct(w.Humidity)}", fInfo, white, 124, 158);
            g.DrawString($"降雨 {Pct(today.Pop)}", fInfo, white, 14, 180);
            var wind = double.IsNaN(w.Wind) ? "--" : ((int)Math.Round(w.Wind)).ToString(CultureInfo.InvariantCulture);
            g.DrawString($"風速 {wind} km/h", fInfo, white, 124, 180);

            // 明天、後天
            for (var i = 1; i <= 2; i++)
            {
                var d = w.Days[i];
                var x = 10 + (i - 1) * 114;
                g.DrawString(d.Label, fDay, soft, x, 211);
                DrawIcon(g, KindOf(d.Code), true, new RectangleF(x + 30, 203, 26, 26));
                g.DrawString($"{Deg(d.Hi)}/{Deg(d.Lo)}", fSmall, white, x + 60, 212);
            }
        }
        return Rgb565.Encode(bmp);
    }

    static void DrawIcon(Graphics g, Kind kind, bool day, RectangleF r)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 100f, r.Height / 100f);
        var light = Color.FromArgb(240, 244, 250);
        var grey = Color.FromArgb(178, 190, 206);
        var dark = Color.FromArgb(140, 152, 174);
        switch (kind)
        {
            case Kind.Clear:
                if (day) Sun(g, 50, 50, 20, 1f);
                else Moon(g, 50, 50, 28);
                break;
            case Kind.Partly:
                if (day) Sun(g, 36, 36, 15, 0.8f);
                else Moon(g, 36, 36, 20);
                Cloud(g, light, 8, 12, 0.92f);
                break;
            case Kind.Cloudy:
                Cloud(g, grey, -4, -8, 0.8f);
                Cloud(g, light, 10, 14, 0.9f);
                break;
            case Kind.Fog:
                Fog(g, light);
                break;
            case Kind.Drizzle:
                Rain(g, grey, 3, 10);
                break;
            case Kind.Rain:
                Rain(g, dark, 4, 15);
                break;
            case Kind.Thunder:
                Thunder(g, dark);
                break;
            case Kind.Snow:
                Snow(g, grey);
                break;
        }
        g.Restore(state);
    }

    // 以下圖示都畫在 100x100 的座標系裡，DrawIcon 負責縮放到實際大小。

    static void Cloud(Graphics g, Color c, float ox, float oy, float k)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, ox + (34 - 18) * k, oy + (52 - 18) * k, 36 * k, 36 * k);
        g.FillEllipse(b, ox + (57 - 24) * k, oy + (42 - 24) * k, 48 * k, 48 * k);
        g.FillEllipse(b, ox + (76 - 14) * k, oy + (54 - 14) * k, 28 * k, 28 * k);
        using var path = RoundRect(ox + 12 * k, oy + 52 * k, 76 * k, 26 * k, 13 * k);
        g.FillPath(b, path);
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Sun(Graphics g, float cx, float cy, float r, float k)
    {
        using (var ray = new Pen(Color.FromArgb(255, 214, 80), 5 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                var cos = (float)Math.Cos(a);
                var sin = (float)Math.Sin(a);
                g.DrawLine(ray, cx + cos * (r + 8 * k), cy + sin * (r + 8 * k),
                    cx + cos * (r + 18 * k), cy + sin * (r + 18 * k));
            }
        }
        using var b = new SolidBrush(Color.FromArgb(255, 200, 40));
        g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
    }

    static void Moon(Graphics g, float cx, float cy, float r)
    {
        using var p1 = new GraphicsPath();
        p1.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
        using var p2 = new GraphicsPath();
        p2.AddEllipse(cx - r + r * 0.55f, cy - r - r * 0.25f, r * 1.8f, r * 1.8f);
        using var reg = new Region(p1);
        reg.Exclude(p2);
        using var b = new SolidBrush(Color.FromArgb(245, 240, 200));
        g.FillRegion(b, reg);
    }

    static void Fog(Graphics g, Color cloud)
    {
        Cloud(g, cloud, 6, -10, 0.88f);
        using var pen = new Pen(Color.FromArgb(214, 220, 228), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, 24, 68, 76, 68);
        g.DrawLine(pen, 18, 80, 70, 80);
        g.DrawLine(pen, 28, 92, 80, 92);
    }

    static void Rain(Graphics g, Color cloud, int drops, float len)
    {
        Cloud(g, cloud, 4, -12, 0.92f);
        using var pen = new Pen(Color.FromArgb(110, 190, 255), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 0; i < drops; i++)
        {
            var x = 28 + i * (44f / (drops - 1));
            g.DrawLine(pen, x + 4, 68, x - 3, 68 + len);
        }
    }

    static void Thunder(Graphics g, Color cloud)
    {
        Cloud(g, cloud, 4, -14, 0.92f);
        using var bolt = new SolidBrush(Color.FromArgb(255, 214, 60));
        g.FillPolygon(bolt, new[]
        {
            new PointF(56, 58), new PointF(38, 82), new PointF(50, 82), new PointF(42, 100),
            new PointF(66, 72), new PointF(53, 72), new PointF(62, 58),
        });
    }

    static void Snow(Graphics g, Color cloud)
    {
        Cloud(g, cloud, 4, -12, 0.92f);
        using var b = new SolidBrush(Color.White);
        g.FillEllipse(b, 27.5f, 75.5f, 9, 9);
        g.FillEllipse(b, 45.5f, 87.5f, 9, 9);
        g.FillEllipse(b, 63.5f, 75.5f, 9, 9);
    }
}
