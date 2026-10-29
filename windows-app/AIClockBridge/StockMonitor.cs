using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// Watchlist quotes. US stocks ("usAAPL") come from Tencent's free endpoint
// (no key, GBK-encoded response, one batch request):
//   http://qt.gtimg.cn/q=usAAPL,usTSLA
// Polled every 5s; the device and the mirror both render the pre-formatted
// strings so the firmware stays dumb (and ASCII-only: it shows the code only).
//
// Taiwan stocks ("tw2330" = TSMC, "tw0050" = ETF, "twT00" = TAIEX) come from
// TWSE's free MIS endpoint instead (Tencent has no TW market):
//   https://mis.twse.com.tw/stock/api/getStockInfo.jsp?ex_ch=tse_2330.tw|otc_2330.tw
// Listed (tse) and OTC (otc) are both queried; only the one that exists answers.
//
// Crypto ("ccBTC", "ccETH", "ccDOGE") comes from Binance's public 24h ticker
// (no key): ccBTC -> BTCUSDT. The change shown is the rolling 24h change.
sealed class StockMonitor
{
    public record Row(string Code, string Name, string Price, string Pct, int Up);

    const string SymbolsKey = "stock_symbols";

    /// Comma-separated symbols: tw=Taiwan, us=US, cc=crypto ("tw2330,usAAPL,ccBTC").
    public static string[] Symbols
    {
        get
        {
            var raw = Settings.Get(SymbolsKey);
            if (raw.Length == 0) raw = "tw2330,tw0050,ccBTC,ccETH";
            return raw.Replace('，', ',') // CN comma happens
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize).ToArray();
        }
        set => Settings.Set(SymbolsKey, string.Join(",", value));
    }

    /// Market prefix must be lowercase but the US ticker must stay UPPERCASE
    /// ("usaapl" gets v_pv_none_match back), so normalize both halves.
    static string Normalize(string s)
    {
        var t = s.Trim();
        if (t.Length <= 2) return t.ToLowerInvariant();
        var prefix = t[..2].ToLowerInvariant();
        var body = t[2..].ToUpperInvariant();
        // crypto: "ccBTC" -> "ccBTCUSDT" (unless a quote asset is already there)
        if (prefix == "cc" && !CryptoQuotes.Any(q => body.EndsWith(q)))
            body += "USDT";
        return prefix + body;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // TWSE MIS wants a session cookie (from index.jsp) before the JSON API answers.
    static readonly HttpClient TwHttp = CreateTwHttp();
    static bool _twWarm;
    const string TwIndexUrl = "https://mis.twse.com.tw/stock/index.jsp";

    static HttpClient CreateTwHttp()
    {
        var h = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AIClockBridge");
        h.DefaultRequestHeaders.Referrer = new Uri(TwIndexUrl);
        return h;
    }

    static readonly string[] CryptoHosts = { "https://api.binance.com", "https://data-api.binance.vision" };
    static readonly string[] CryptoQuotes = { "FDUSD", "USDT", "USDC" };

    static bool IsCrypto(string symbol) =>
        symbol.StartsWith("cc", StringComparison.OrdinalIgnoreCase) && symbol.Length > 2;

    static bool IsTw(string symbol) =>
        symbol.StartsWith("tw", StringComparison.OrdinalIgnoreCase) && symbol.Length > 2;
    static readonly Encoding Gbk;

    static StockMonitor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding enc;
        try { enc = Encoding.GetEncoding("GB18030"); }
        catch { enc = Encoding.Latin1; } // names garble, numbers still parse
        Gbk = enc;
    }

    readonly object _lock = new();
    Row[] _rows = Array.Empty<Row>();
    // last good row per lowercase symbol, so one source failing keeps its old values
    readonly Dictionary<string, Row> _cache = new();
    System.Threading.Timer _timer;

    // The device's font is ASCII-only, so CJK company names go down as
    // rendered RGB565 strips (same trick as the music title strip): one
    // NameW x NameH strip per row, wire format [1 byte count][strips...].
    // names_rev in /stock tells the device when to re-fetch.
    public const int NameW = 156, NameH = 16;
    int _namesRev;
    byte[] _namesData = { 0 };
    string _lastNamesKey = "";

    public void Start()
    {
        _ = Fetch();
        _timer = new System.Threading.Timer(_ => _ = Fetch(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public Row[] Snapshot
    {
        get { lock (_lock) return _rows; }
    }

    public byte[] ToJson()
    {
        var stocks = Snapshot.Select(r => new Dictionary<string, object>
        {
            ["code"] = r.Code, ["name"] = r.Name, ["price"] = r.Price,
            ["pct"] = r.Pct, ["up"] = r.Up,
        }).ToArray();
        int rev;
        lock (_lock) rev = _namesRev;
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["stocks"] = stocks, ["names_rev"] = rev,
        });
    }

    /// [1 byte count][NameW x NameH RGB565 big-endian per row...]
    public byte[] NamesRgb565
    {
        get { lock (_lock) return _namesData; }
    }

    void RenderNamesIfNeeded(Row[] parsed)
    {
        var top = parsed.Take(4).ToArray();
        var key = string.Join("\n", top.Select(r => r.Name));
        lock (_lock) if (key == _lastNamesKey) return;
        using var ms = new MemoryStream();
        ms.WriteByte((byte)top.Length);
        foreach (var row in top)
        {
            using var bmp = new Bitmap(NameW, NameH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Microsoft JhengHei UI", 9f);
                using var brush = new SolidBrush(Color.FromArgb(184, 184, 184));
                using var right = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                g.DrawString(row.Name, font, brush, new RectangleF(0, 0, NameW, NameH), right);
            }
            ms.Write(Rgb565.Encode(bmp));
        }
        lock (_lock)
        {
            _lastNamesKey = key;
            _namesData = ms.ToArray();
            _namesRev++;
        }
    }

    async Task Fetch()
    {
        var symbols = Symbols;
        if (symbols.Length == 0)
        {
            lock (_lock) _rows = Array.Empty<Row>();
            return;
        }
        var gtimg = symbols.Where(x => !IsTw(x) && !IsCrypto(x)).ToArray();
        var tw = symbols.Where(IsTw).ToArray();
        var cc = symbols.Where(IsCrypto).ToArray();

        if (gtimg.Length > 0)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync("http://qt.gtimg.cn/q=" + string.Join(",", gtimg));
                Merge(gtimg, Parse(Gbk.GetString(bytes)));
            }
            catch { /* transient network error: keep the previous rows */ }
        }
        if (tw.Length > 0)
        {
            try { Merge(tw, await FetchTw(tw)); }
            catch { _twWarm = false; /* keep previous rows, re-warm the session next time */ }
        }
        if (cc.Length > 0)
        {
            try { Merge(cc, await FetchCrypto(cc)); }
            catch { /* keep previous rows */ }
        }

        Row[] rows;
        lock (_lock)
        {
            rows = symbols.Select(x => x.ToLowerInvariant())
                .Where(_cache.ContainsKey).Select(x => _cache[x]).ToArray();
            _rows = rows;
        }
        RenderNamesIfNeeded(rows);
    }

    /// A source answered: replace its symbols (drop ones it no longer knows).
    void Merge(string[] source, Dictionary<string, Row> fresh)
    {
        lock (_lock)
        {
            foreach (var x in source)
            {
                var k = x.ToLowerInvariant();
                if (fresh.TryGetValue(k, out var row)) _cache[k] = row;
                else _cache.Remove(k);
            }
        }
    }

    /// TWSE MIS batch quote. JSON: msgArray[] of { c=code, n=name, z=last trade
    /// ("-" before the first trade), y=previous close, ... } - no change fields,
    /// so change/% are computed from z and y.
    async Task<Dictionary<string, Row>> FetchTw(string[] symbols)
    {
        var channels = symbols.SelectMany(TwChannels).Distinct();
        var query = Uri.EscapeDataString(string.Join("|", channels));
        for (var attempt = 0; ; attempt++)
        {
            if (!_twWarm)
            {
                using var warm = await TwHttp.GetAsync(TwIndexUrl);
                _twWarm = true;
            }
            var url = "https://mis.twse.com.tw/stock/api/getStockInfo.jsp?json=1&delay=0&ex_ch=" + query
                      + "&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = await TwHttp.GetStringAsync(url);
            var map = ParseTw(json);
            if (map.Count > 0 || attempt >= 1) return map;
            _twWarm = false; // empty answer: session probably expired, re-warm once
        }
    }

    /// One Binance 24h-ticker request per symbol (a single bad symbol would fail
    /// a batch request). Tries the main API, then the market-data mirror. A symbol
    /// that cannot be fetched keeps its last good row instead of vanishing.
    async Task<Dictionary<string, Row>> FetchCrypto(string[] symbols)
    {
        var tasks = symbols.Select(async sym =>
        {
            var key = sym.ToLowerInvariant();
            var pair = sym[2..].ToUpperInvariant();
            foreach (var host in CryptoHosts)
            {
                try
                {
                    var json = await Http.GetStringAsync($"{host}/api/v3/ticker/24hr?symbol={pair}");
                    return (K: key, R: ParseCrypto(json, pair));
                }
                catch { /* try the next host */ }
            }
            Row old = null;
            lock (_lock) _cache.TryGetValue(key, out old);
            return (K: key, R: old);
        });
        var results = await Task.WhenAll(tasks);
        return results.Where(x => x.R != null).ToDictionary(x => x.K, x => x.R);
    }

    static Row ParseCrypto(string json, string pair)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        if (!double.TryParse(S("lastPrice"), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) ||
            !double.TryParse(S("priceChange"), NumberStyles.Float, CultureInfo.InvariantCulture, out var chg) ||
            !double.TryParse(S("priceChangePercent"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            throw new FormatException("bad ticker");
        var quote = CryptoQuotes.FirstOrDefault(q => pair.EndsWith(q)) ?? "";
        var asset = quote.Length > 0 ? pair[..^quote.Length] : pair;
        return new Row(asset, quote.Length > 0 ? $"{asset}/{quote}" : asset, FormatCryptoPrice(price),
            $"{pct:+0.00;-0.00;+0.00}%", chg > 0 ? 1 : (chg < 0 ? -1 : 0));
    }

    /// Enough decimals for small coins, but short enough for the 240px row.
    static string FormatCryptoPrice(double p)
    {
        if (p >= 10000) return $"{p:F0}";
        if (p >= 1000) return $"{p:F1}";
        if (p >= 1) return $"{p:F2}";
        if (p >= 0.01) return $"{p:F4}";
        return $"{p:F6}";
    }

    /// "tw2330" -> tse_2330.tw + otc_2330.tw; "twT00" (TAIEX index) -> tse_t00.tw
    static IEnumerable<string> TwChannels(string symbol)
    {
        var body = symbol[2..].ToUpperInvariant();
        if (body == "T00") return new[] { "tse_t00.tw" };
        return new[] { $"tse_{body}.tw", $"otc_{body}.tw" };
    }

    static Dictionary<string, Row> ParseTw(string json)
    {
        var map = new Dictionary<string, Row>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("msgArray", out var arr)) return map;
        foreach (var e in arr.EnumerateArray())
        {
            string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var code = S("c");
            if (code.Length == 0 ||
                !double.TryParse(S("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var prev) ||
                prev <= 0) continue;
            // before the first trade z is "-": show the previous close, flat
            if (!double.TryParse(S("z"), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) ||
                price <= 0) price = prev;
            var chg = price - prev;
            var pct = chg / prev * 100;
            var name = S("n");
            map["tw" + code.ToLowerInvariant()] = new Row(
                code.ToUpperInvariant(), name.Length > 0 ? name : code, FormatPrice(price),
                $"{pct:+0.00;-0.00;+0.00}%", chg > 1e-9 ? 1 : (chg < -1e-9 ? -1 : 0));
        }
        return map;
    }

    /// Response is lines of `v_usAAPL="200~...~...";`
    /// fields split by "~": [3]=price [31]=change [32]=change%. The name field is
    /// Simplified Chinese, so the ticker is shown instead.
    static Dictionary<string, Row> Parse(string text)
    {
        var bySymbol = new Dictionary<string, Row>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq < 0 || !line.StartsWith("v_")) continue;
            var symbol = line[2..eq].ToLowerInvariant();
            var f = line[(eq + 1)..].Trim('"', ';', '\r').Split('~');
            if (f.Length <= 32 ||
                !double.TryParse(f[3], out var price) ||
                !double.TryParse(f[31], out var chg) ||
                !double.TryParse(f[32], out var pct)) continue;
            // lowercase key so the case-normalized query symbol still matches
            bySymbol[symbol] = new Row(
                DisplayCode(symbol), DisplayCode(symbol), FormatPrice(price),
                $"{pct:+0.00;-0.00}%", chg > 0 ? 1 : (chg < 0 ? -1 : 0));
        }
        return bySymbol;
    }

    /// "usAAPL" -> "AAPL"
    static string DisplayCode(string symbol) =>
        symbol.StartsWith("us") && symbol.Length > 2
            ? symbol[2..].ToUpperInvariant()
            : symbol.ToUpperInvariant();

    static string FormatPrice(double p)
    {
        if (p >= 10000) return $"{p:F0}";
        if (p >= 1000) return $"{p:F1}";
        return $"{p:F2}";
    }
}
