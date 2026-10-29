namespace AIClockBridge;

// Custom auto-rotation: instead of the firmware's own "auto" mode (which only
// follows whichever AI app is busy), the bridge cycles the device through the
// pages the user ticked, one every N seconds, by POSTing /api/display with the
// firmware modes claude | codex | net | music | stock.
// Settings live in settings.json: rotate_enabled ("1"/"0"), rotate_pages
// ("claude,net,stock"), rotate_secs ("10").
sealed class AutoRotation
{
    public static readonly (string Mode, string Title)[] Pages =
    {
        ("claude", "Claude 額度"),
        ("codex", "Codex 額度"),
        ("net", "網速曲線"),
        ("music", "音樂播放"),
        ("weather", "天氣預報"),
        ("stock", "股票行情"),
    };

    static readonly int[] IntervalChoices = { 5, 10, 15, 30, 60, 120 };

    const string EnabledKey = "rotate_enabled";
    const string PagesKey = "rotate_pages";
    const string SecsKey = "rotate_secs";
    const int MinSecs = 3, MaxSecs = 3600;

    public static bool Enabled
    {
        // never configured -> on: the feature was asked for, so don't make the
        // user hunt for a hidden switch before anything happens
        get { var raw = Settings.Get(EnabledKey); return raw.Length == 0 || raw == "1"; }
        set => Settings.Set(EnabledKey, value ? "1" : "0");
    }

    public static int Seconds
    {
        get
        {
            if (!int.TryParse(Settings.Get(SecsKey), out var s)) s = 10;
            return Math.Clamp(s, MinSecs, MaxSecs);
        }
        set => Settings.Set(SecsKey, Math.Clamp(value, MinSecs, MaxSecs).ToString());
    }

    /// Selected firmware modes, always in the fixed order of <see cref="Pages"/>.
    public static string[] SelectedModes
    {
        get
        {
            var raw = Settings.Get(PagesKey);
            if (raw.Length == 0) raw = "claude,codex,net,stock"; // first-run default
            var chosen = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Pages.Select(p => p.Mode).Where(m => chosen.Contains(m)).ToArray();
        }
    }

    readonly System.Windows.Forms.Timer _timer = new();
    readonly Dictionary<string, ToolStripMenuItem> _pageItems = new();
    readonly Dictionary<int, ToolStripMenuItem> _secItems = new();
    ToolStripMenuItem _enableItem;
    ToolStripMenuItem _statusItem;
    string _lastMsg = "";
    ToolStripMenuItem _customSecItem;
    int _index = -1;
    bool _busy;

    public void Start()
    {
        _timer.Interval = Seconds * 1000;
        _timer.Tick += async (_, _) => await Step();
        _timer.Start();
    }

    /// User picked a fixed page by hand: stop rotating so the choice sticks.
    public void Disable()
    {
        Enabled = false;
        _lastMsg = "已因手動切換頁面而暫停，要繼續請重新勾選「啟用輪播」";
        SyncChecks();
    }

    async Task Step()
    {
        if (_busy || !Enabled) return;
        if (DeviceClient.Host.Length == 0)
        {
            _lastMsg = "未設定裝置地址（先用「自動查詢並配對裝置」）";
            return;
        }
        var modes = SelectedModes;
        if (modes.Length == 0)
        {
            _lastMsg = "沒有勾選任何頁面";
            return;
        }
        _busy = true;
        try
        {
            _index = (_index + 1) % modes.Length;
            var next = modes[_index];
            var title = Pages.First(p => p.Mode == next).Title;
            if (modes.Length == 1)
            {
                // a single page: only (re)send when the device is not on it, e.g.
                // after a reboot; re-sending every tick would redraw and flicker
                var info = await DeviceClient.FetchInfo();
                if (info.Mode == next)
                {
                    _lastMsg = $"停留在 {title}";
                    return;
                }
            }
            await DeviceClient.SetDisplayMode(next);
            _lastMsg = $"{DateTime.Now:HH:mm:ss} 已切換到 {title}";
        }
        catch (Exception e)
        {
            // device offline / busy: try again on the next tick
            _lastMsg = $"{DateTime.Now:HH:mm:ss} 切換失敗：{e.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    public ToolStripMenuItem BuildMenu()
    {
        var root = new ToolStripMenuItem("自動輪播");
        root.DropDownOpening += (_, _) => SyncChecks();

        _statusItem = new ToolStripMenuItem("狀態：…") { Enabled = false };
        root.DropDownItems.Add(_statusItem);

        _enableItem = new ToolStripMenuItem("啟用輪播") { CheckOnClick = true };
        _enableItem.Click += async (_, _) =>
        {
            Enabled = _enableItem.Checked; // CheckOnClick already flipped it
            _lastMsg = "";
            SyncChecks();
            if (Enabled) await Step();
        };
        root.DropDownItems.Add(_enableItem);
        root.DropDownItems.Add(new ToolStripSeparator());

        foreach (var (mode, title) in Pages)
        {
            var item = new ToolStripMenuItem(title) { CheckOnClick = true };
            item.Click += (_, _) => SavePages();
            _pageItems[mode] = item;
            root.DropDownItems.Add(item);
        }
        root.DropDownItems.Add(new ToolStripSeparator());

        var secMenu = new ToolStripMenuItem("每頁停留時間");
        foreach (var secs in IntervalChoices)
        {
            var item = new ToolStripMenuItem($"{secs} 秒");
            var s = secs;
            item.Click += (_, _) => ApplySeconds(s);
            _secItems[secs] = item;
            secMenu.DropDownItems.Add(item);
        }
        _customSecItem = new ToolStripMenuItem("自定義…");
        _customSecItem.Click += (_, _) =>
        {
            var input = InputDialog.Show("輪播間隔",
                $"每頁停留的秒數（{MinSecs}–{MaxSecs}）", Seconds.ToString(), "10");
            if (input != null && int.TryParse(input.Trim(), out var v)) ApplySeconds(v);
        };
        secMenu.DropDownItems.Add(_customSecItem);
        root.DropDownItems.Add(secMenu);

        SyncChecks();
        return root;
    }

    void SavePages()
    {
        var chosen = _pageItems.Where(kv => kv.Value.Checked).Select(kv => kv.Key);
        // empty string would fall back to the default list, so store a marker
        var raw = string.Join(",", Pages.Select(p => p.Mode).Where(m => chosen.Contains(m)));
        Settings.Set(PagesKey, raw.Length == 0 ? "none" : raw);
    }

    void ApplySeconds(int secs)
    {
        Seconds = secs;
        _timer.Interval = Seconds * 1000;
        SyncChecks();
    }

    /// Re-read settings into the checkmarks (menu opening, or after Disable()).
    public void SyncChecks()
    {
        if (_enableItem == null) return;
        _enableItem.Checked = Enabled;
        _statusItem.Text = "狀態：" + (_lastMsg.Length > 0 ? _lastMsg
            : (Enabled ? $"執行中，每 {Seconds} 秒換頁" : "未啟用"));
        var modes = SelectedModes;
        foreach (var (mode, item) in _pageItems) item.Checked = modes.Contains(mode);
        var cur = Seconds;
        foreach (var (secs, item) in _secItems) item.Checked = secs == cur;
        _customSecItem.Checked = !_secItems.ContainsKey(cur);
    }
}
