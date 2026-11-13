using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>
/// Programın merkezi: görev çubuğu simgesi, ana pencere, oyun paneli ve sensör döngüsü.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string TaskName = "SicaklikPaneli";
    private const int HotkeyId = 1;
    private const int HistorySeconds = 180;

    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly OverlayForm _overlay;
    private readonly DashboardForm _dashboard;
    private readonly HotkeyWindow _hotkeys;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Snapshot> _history = new();
    private IntPtr _lastIconHandle;
    private bool _warnedNoCpuTemp;
    private int _readCount;
    private volatile bool _rtssActive;
    private readonly bool _rtssInstalled = RtssOsd.FindExe() != null;

    private ToolStripMenuItem _modeAuto = null!, _modeAlways = null!, _modeOff = null!, _autostart = null!;
    private readonly Dictionary<Corner, ToolStripMenuItem> _cornerItems = new();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _overlay = new OverlayForm { Opacity = Math.Clamp(_settings.Opacity, 20, 99) / 100.0 };
        _ = _overlay.Handle; // pencere tutamacını oluştur (gizli)

        _dashboard = new DashboardForm();
        _dashboard.View.HistoryCapacity = HistorySeconds;
        _dashboard.View.Mode = _settings.Mode;
        _dashboard.View.RtssInstalled = _rtssInstalled;
        _dashboard.View.ModeSelected += SetMode;

        _tray = new NotifyIcon { Visible = true, Text = "Sıcaklık Paneli – başlatılıyor…", ContextMenuStrip = BuildMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _dashboard.ShowAndActivate(); };
        UpdateTrayIcon(null);

        _hotkeys = new HotkeyWindow();
        // Ctrl + Shift + F10: oyun paneli modunu değiştir
        Native.RegisterHotKey(_hotkeys.Handle, HotkeyId, 0x2 | 0x4, (uint)Keys.F10);
        _hotkeys.Pressed += CycleMode;

        // Panelin görünürlüğünü saniyede 2 kez kontrol et
        _uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiTimer.Tick += (_, _) => UpdateOverlayVisibility();
        _uiTimer.Start();

        Task.Run(() => SensorLoop(_cts.Token));

        // Elle açıldıysa ana pencereyi göster; Windows ile başladıysa simge durumunda kal
        if (!Environment.GetCommandLineArgs().Contains("--gizli", StringComparer.OrdinalIgnoreCase))
            _dashboard.Show();
    }

    // ---------------- Sensör okuma (arka planda) ----------------

    private void SensorLoop(CancellationToken ct)
    {
        Sensors? sensors = null;
        try
        {
            sensors = new Sensors();
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                Snapshot snap;
                try { snap = sensors.Read(); }
                catch { snap = Snapshot.Empty; }

                // Oyun içi gösterge (RTSS): özel tam ekranda da görünür
                if (_settings.Mode != PanelMode.Kapali)
                {
                    if (tick % 30 == 0) RtssOsd.EnsureStarted(); // RTSS kapalıysa başlat
                    _rtssActive = RtssOsd.Update(RtssOsd.Format(snap));
                }
                else if (_rtssActive)
                {
                    RtssOsd.Release();
                    _rtssActive = false;
                }
                tick++;

                _ui.Post(_ => OnData(snap), null);
                ct.WaitHandle.WaitOne(1000);
            }
        }
        catch (Exception ex)
        {
            _ui.Post(_ => MessageBox.Show("Sensörler açılamadı:\n" + ex.Message, "Sıcaklık Paneli",
                MessageBoxButtons.OK, MessageBoxIcon.Error), null);
        }
        finally { sensors?.Dispose(); }
    }

    private void OnData(Snapshot s)
    {
        _history.Add(s);
        if (_history.Count > HistorySeconds) _history.RemoveAt(0);

        UpdateTrayIcon(s);
        _overlay.SetData(s);

        var view = _dashboard.View;
        view.Data = s;
        view.History = _history;
        view.RtssConnected = _rtssActive;
        if (_dashboard.Visible) view.Invalidate();

        static string Fmt(float? t, float? l, bool throttle) =>
            $"{(t.HasValue ? $"{t:0}°C" : "--")}  {(l.HasValue ? $"%{l:0}" : "")}{(throttle ? "  ⚠ kısıtlama" : "")}";
        string tip = $"CPU: {Fmt(s.CpuTemp, s.CpuLoad, s.CpuThrottle)}\nGPU: {Fmt(s.GpuTemp, s.GpuLoad, s.GpuThrottle)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        // Birkaç okumadan sonra hâlâ CPU sıcaklığı yoksa sürücü eksik demektir
        if (++_readCount == 3 && s.CpuTemp == null && !_warnedNoCpuTemp)
        {
            _warnedNoCpuTemp = true;
            _tray.ShowBalloonTip(10000, "CPU sıcaklığı okunamıyor",
                "PawnIO sürücüsü kurulu değil gibi görünüyor. Kurmak için: winget install namazso.PawnIO  (sonra programı yeniden başlatın)",
                ToolTipIcon.Warning);
        }
    }

    // ---------------- Görev çubuğu simgesi ----------------

    private void UpdateTrayIcon(Snapshot? s)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            // Büyük beyaz rakamlar simgenin tamamını kaplar, renk alttaki çizgiyle gösterilir
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = size.Width, h = size.Height;
            int bar = Math.Max(2, h / 8);
            string text = s?.CpuTemp is float t ? Math.Min(t, 99).ToString("0") : "--";

            using var path = new GraphicsPath();
            using var family = new FontFamily("Arial");
            path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var bb = path.GetBounds();
            var target = text == "--"
                ? new RectangleF(1, h * 0.4f, w - 2, h * 0.15f)
                : new RectangleF(0, 0, w, h - bar - 1);
            using (var m = new Matrix())
            {
                m.Translate(target.X, target.Y);
                m.Scale(target.Width / bb.Width, target.Height / bb.Height);
                m.Translate(-bb.X, -bb.Y);
                path.Transform(m);
            }
            g.FillPath(Brushes.White, path);

            g.SmoothingMode = SmoothingMode.None;
            bool throttle = s is { CpuThrottle: true } or { GpuThrottle: true };
            using var barBrush = new SolidBrush(throttle ? Theme.Warning : Theme.TempColor(s?.CpuTemp));
            g.FillRectangle(barBrush, 0, h - bar, w, bar);
        }

        IntPtr hIcon = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(hIcon);
        old?.Dispose();
        if (_lastIconHandle != IntPtr.Zero) Native.DestroyIcon(_lastIconHandle);
        _lastIconHandle = hIcon;
    }

    // ---------------- Oyun paneli ----------------

    private void UpdateOverlayVisibility()
    {
        bool fullscreen = Native.IsForegroundFullscreen(out Screen? screen);
        // RTSS bağlıysa oyundaki göstergeyi o çizer; pencere panelini tam ekranda gizle (çift görünmesin)
        bool show = _settings.Mode switch
        {
            PanelMode.HerZaman => !(fullscreen && _rtssActive),
            PanelMode.Otomatik => fullscreen && !_rtssActive,
            _ => false
        };

        if (show)
        {
            _overlay.PlaceOn(screen ?? Screen.PrimaryScreen!, _settings.Corner);
            if (!_overlay.Visible) _overlay.Show();
            _overlay.BringToTop();
        }
        else if (_overlay.Visible)
        {
            _overlay.Hide();
        }
    }

    private void CycleMode()
    {
        SetMode(_settings.Mode switch
        {
            PanelMode.Otomatik => PanelMode.HerZaman,
            PanelMode.HerZaman => PanelMode.Kapali,
            _ => PanelMode.Otomatik
        });
        string name = _settings.Mode switch
        {
            PanelMode.Otomatik => "Otomatik (tam ekranda açılır)",
            PanelMode.HerZaman => "Her zaman açık",
            _ => "Kapalı"
        };
        _tray.ShowBalloonTip(1500, "Oyun paneli", name, ToolTipIcon.None);
    }

    private void SetMode(PanelMode m)
    {
        _settings.Mode = m;
        _settings.Save();
        _modeAuto.Checked = m == PanelMode.Otomatik;
        _modeAlways.Checked = m == PanelMode.HerZaman;
        _modeOff.Checked = m == PanelMode.Kapali;
        _dashboard.View.Mode = m;
        _dashboard.View.Invalidate();
        UpdateOverlayVisibility();
    }

    private void SetCorner(Corner c)
    {
        _settings.Corner = c;
        _settings.Save();
        foreach (var (k, item) in _cornerItems) item.Checked = k == c;
        UpdateOverlayVisibility();
    }

    // ---------------- Menü ----------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Sıcaklık Paneli'ni aç", null, (_, _) => _dashboard.ShowAndActivate());
        open.Font = new Font(open.Font, FontStyle.Bold);

        var panel = new ToolStripMenuItem("Oyun paneli");
        _modeAuto = new ToolStripMenuItem("Otomatik (tam ekran oyunda)", null, (_, _) => SetMode(PanelMode.Otomatik));
        _modeAlways = new ToolStripMenuItem("Her zaman göster", null, (_, _) => SetMode(PanelMode.HerZaman));
        _modeOff = new ToolStripMenuItem("Kapalı", null, (_, _) => SetMode(PanelMode.Kapali));
        panel.DropDownItems.AddRange([_modeAuto, _modeAlways, _modeOff]);
        _modeAuto.Checked = _settings.Mode == PanelMode.Otomatik;
        _modeAlways.Checked = _settings.Mode == PanelMode.HerZaman;
        _modeOff.Checked = _settings.Mode == PanelMode.Kapali;

        var corner = new ToolStripMenuItem("Panel konumu");
        foreach (var (c, label) in new[] { (Corner.SolUst, "Sol üst"), (Corner.SagUst, "Sağ üst"),
                                           (Corner.SolAlt, "Sol alt"), (Corner.SagAlt, "Sağ alt") })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetCorner(c)) { Checked = _settings.Corner == c };
            _cornerItems[c] = item;
            corner.DropDownItems.Add(item);
        }

        var opacity = new ToolStripMenuItem("Panel saydamlığı");
        foreach (int o in new[] { 50, 65, 80, 95 })
        {
            var item = new ToolStripMenuItem($"%{o}", null, (s, _) =>
            {
                _settings.Opacity = o; _settings.Save();
                _overlay.Opacity = o / 100.0;
                foreach (ToolStripMenuItem i in opacity.DropDownItems) i.Checked = i == s;
            }) { Checked = _settings.Opacity == o };
            opacity.DropDownItems.Add(item);
        }

        _autostart = new ToolStripMenuItem("Windows ile başlat", null, (_, _) => ToggleAutostart()) { Checked = IsAutostartEnabled() };

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.AddRange([panel, corner, opacity, _autostart]);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());
        return menu;
    }

    // ---------------- Otomatik başlatma (yönetici izniyle, Görev Zamanlayıcı) ----------------

    private static bool IsAutostartEnabled() => RunSchtasks($"/query /tn \"{TaskName}\"") == 0;

    private void ToggleAutostart()
    {
        int rc;
        if (IsAutostartEnabled())
            rc = RunSchtasks($"/delete /tn \"{TaskName}\" /f");
        else
        {
            string exe = Environment.ProcessPath!;
            rc = RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --gizli\" /sc onlogon /rl highest /f");
        }
        if (rc != 0)
            MessageBox.Show("Otomatik başlatma ayarı değiştirilemedi.", "Sıcaklık Paneli", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _autostart.Checked = IsAutostartEnabled();
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    // ---------------- Kapanış ----------------

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _uiTimer.Stop();
        RtssOsd.Release();
        Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
        _hotkeys.DestroyHandle();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        _dashboard.Dispose();
        if (_lastIconHandle != IntPtr.Zero) Native.DestroyIcon(_lastIconHandle);
        base.ExitThreadCore();
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action? Pressed;
        public HotkeyWindow() => CreateHandle(new CreateParams());
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) Pressed?.Invoke(); // WM_HOTKEY
            base.WndProc(ref m);
        }
    }
}
