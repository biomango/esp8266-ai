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
        ("claude", "Claude 额度"),
        ("codex", "Codex 额度"),
        ("net", "网速曲线"),
        ("music", "音乐播放"),
        ("stock", "股票行情"),
    };

    static readonly int[] IntervalChoices = { 5, 10, 15, 30, 60, 120 };

    const string EnabledKey = "rotate_enabled";
    const string PagesKey = "rotate_pages";
    const string SecsKey = "rotate_secs";
    const int MinSecs = 3, MaxSecs = 3600;

    public static bool Enabled
    {
        get => Settings.Get(EnabledKey) == "1";
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
        SyncChecks();
    }

    async Task Step()
    {
        if (_busy || !Enabled || DeviceClient.Host.Length == 0) return;
        var modes = SelectedModes;
        if (modes.Length == 0) return;
        _busy = true;
        try
        {
            _index = (_index + 1) % modes.Length;
            var next = modes[_index];
            if (modes.Length == 1)
            {
                // a single page: only (re)send when the device is not on it, e.g.
                // after a reboot; re-sending every tick would redraw and flicker
                var info = await DeviceClient.FetchInfo();
                if (info.Mode == next) return;
            }
            await DeviceClient.SetDisplayMode(next);
        }
        catch
        {
            // device offline / busy: try again on the next tick
        }
        finally
        {
            _busy = false;
        }
    }

    public ToolStripMenuItem BuildMenu()
    {
        var root = new ToolStripMenuItem("自动轮播");

        _enableItem = new ToolStripMenuItem("启用轮播") { CheckOnClick = true };
        _enableItem.Click += async (_, _) =>
        {
            Enabled = _enableItem.Checked; // CheckOnClick already flipped it
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

        var secMenu = new ToolStripMenuItem("每页停留时间");
        foreach (var secs in IntervalChoices)
        {
            var item = new ToolStripMenuItem($"{secs} 秒");
            var s = secs;
            item.Click += (_, _) => ApplySeconds(s);
            _secItems[secs] = item;
            secMenu.DropDownItems.Add(item);
        }
        _customSecItem = new ToolStripMenuItem("自定义…");
        _customSecItem.Click += (_, _) =>
        {
            var input = InputDialog.Show("轮播间隔",
                $"每页停留的秒数（{MinSecs}–{MaxSecs}）", Seconds.ToString(), "10");
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
        var modes = SelectedModes;
        foreach (var (mode, item) in _pageItems) item.Checked = modes.Contains(mode);
        var cur = Seconds;
        foreach (var (secs, item) in _secItems) item.Checked = secs == cur;
        _customSecItem.Checked = !_secItems.ContainsKey(cur);
    }
}
