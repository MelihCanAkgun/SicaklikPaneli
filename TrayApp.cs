using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace SicaklikPaneli;

/// <summary>
/// Programın merkezi: görev çubuğu simgesi, ana pencere, oyun paneli ve sensör döngüsü.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string TaskName = "DragonSaj";
    private const string OldTaskName = "SicaklikPaneli"; // eski sürümün otomatik başlatma görevi
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

    // Fan komutları arayüzden fan iş parçacığına bu kuyrukla aktarılır (EC'ye tek yerden erişilir).
    // Fan iş parçacığı sensör okumasından ayrıdır; yavaş bir sensör okuması tıklamaları bekletmez.
    private readonly ConcurrentQueue<(int seq, Action<FanController> run)> _fanCommands = new();
    private readonly AutoResetEvent _wake = new(false);
    private int _commandSeq;
    private volatile FanState _fanState = FanState.Unavailable("Fan bilgisi bekleniyor…");
    private volatile Snapshot _lastSensors = Snapshot.Empty;
    private DateTime _lastHistory = DateTime.MinValue;

    private ToolStripMenuItem _modeAuto = null!, _modeAlways = null!, _modeOff = null!, _autostart = null!,
                              _perfTurbo = null!, _perfBalanced = null!;
    private readonly Dictionary<Corner, ToolStripMenuItem> _cornerItems = new();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        MigrateAutostart();

        _overlay = new OverlayForm { Opacity = Math.Clamp(_settings.Opacity, 20, 99) / 100.0 };
        _ = _overlay.Handle; // pencere tutamacını oluştur (gizli)

        _dashboard = new DashboardForm();
        _dashboard.View.HistoryCapacity = HistorySeconds;
        _dashboard.View.Mode = _settings.Mode;
        _dashboard.SetRtss(false, _rtssInstalled);
        _dashboard.View.ModeSelected += SetMode;
        _dashboard.View.PerfSelected += SetPerfMode;
        WireFanPage();
        WireOcPage();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tray = new NotifyIcon { Visible = true, Text = "Dragon Saj – başlatılıyor…", ContextMenuStrip = BuildMenu() };
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
        Task.Run(() => FanLoop(_cts.Token));

        // Elle açıldıysa ana pencereyi göster; Windows ile başladıysa simge durumunda kal
        if (!Environment.GetCommandLineArgs().Contains("--gizli", StringComparer.OrdinalIgnoreCase))
            _dashboard.Show();
    }

    // ---------------- Fanlar ----------------

    private void WireFanPage()
    {
        var fans = _dashboard.Fans;
        fans.ProtectionEnabled = _settings.FanProtection;
        fans.ProtectionTemp = _settings.FanProtectionTemp;

        if (_settings is { CpuFanTemps: not null, CpuFanSpeeds: not null, GpuFanTemps: not null, GpuFanSpeeds: not null })
            fans.SavedCustom = (new FanCurve(_settings.CpuFanTemps, _settings.CpuFanSpeeds),
                                new FanCurve(_settings.GpuFanTemps, _settings.GpuFanSpeeds));

        fans.ApplyRequested += (cpu, gpu) =>
        {
            fans.SavedCustom = (cpu.Copy(), gpu.Copy());
            _settings.FanStartup = FanStartup.Ozel;
            _settings.CpuFanTemps = cpu.Temps; _settings.CpuFanSpeeds = cpu.Speeds;
            _settings.GpuFanTemps = gpu.Temps; _settings.GpuFanSpeeds = gpu.Speeds;
            _settings.Save();
            FanCommand(f => f.ApplyCustom(cpu, gpu));
        };
        fans.AutoRequested += () =>
        {
            _settings.FanStartup = FanStartup.Otomatik;
            _settings.Save();
            FanCommand(f => f.SetAuto());
        };
        fans.BoostRequested += on => FanCommand(f => f.SetManualBoost(on));
        fans.FixedTarget = Math.Clamp(_settings.FanFixedRpm, FanController.FixedMinRpm, FanController.FixedMaxRpm);
        fans.FixedRequested += rpm =>
        {
            _settings.FanStartup = FanStartup.SabitDevir;
            _settings.FanFixedRpm = rpm;
            _settings.Save();
            FanCommand(f => f.SetFixedRpm(rpm));
        };
        fans.ProtectionChanged += (enabled, temp) =>
        {
            _settings.FanProtection = enabled;
            _settings.FanProtectionTemp = temp;
            _settings.Save();
            _wake.Set();
        };
    }

    // ---------------- Ekran kartı hız aşırtma ----------------

    private void WireOcPage()
    {
        var perf = _dashboard.Perf;
        perf.PerfSelected += SetPerfMode;
        perf.OcRequested += ApplyOc;
        perf.CoreTarget = Math.Clamp(_settings.GpuOcCore, 0, PerfView.CoreLimit);
        perf.MemoryTarget = Math.Clamp(_settings.GpuOcMemory, 0, PerfView.MemoryLimit);
        perf.Oc = GpuOverclock.Read();

        if (_settings.GpuOcEnabled)
        {
            if (_settings.GpuOcCleanExit)
                ApplyOc(true, perf.CoreTarget, perf.MemoryTarget);
            else
            {
                // Önceki oturum hız aşırtma açıkken düzgün kapanmadı (çökme olabilir): yeniden uygulama
                _settings.GpuOcEnabled = false;
                _settings.GpuOcCleanExit = true;
                _settings.Save();
                perf.Message = "Önceki oturum düzgün kapanmadığı için hız aşırtma güvenlik amacıyla kapatıldı.";
            }
        }
        SystemEvents.SessionEnding += (_, _) => ResetOcOnExit();
    }

    private void ApplyOc(bool enabled, int core, int memory)
    {
        var perf = _dashboard.Perf;
        core = Math.Clamp(core, 0, PerfView.CoreLimit);
        memory = Math.Clamp(memory, 0, PerfView.MemoryLimit);

        bool ok = GpuOverclock.Apply(enabled ? core : 0, enabled ? memory : 0, out string error);
        bool active = ok && enabled;

        _settings.GpuOcEnabled = active;
        _settings.GpuOcCore = core;
        _settings.GpuOcMemory = memory;
        _settings.GpuOcCleanExit = !active; // açıkken "temiz kapanmadı" say; kapanışta düzeltilir
        _settings.Save();

        perf.Oc = GpuOverclock.Read();
        perf.OcEnabled = active;
        perf.Message = !ok ? "Hız aşırtma uygulanamadı: " + error
                     : enabled ? $"Hız aşırtma uygulandı: çekirdek +{core} MHz, bellek +{memory} MHz."
                     : "Hız aşırtma kapatıldı.";
        perf.Invalidate();
    }

    /// <summary>Program ya da Windows kapanırken ofsetleri sıfırlar ve oturumu "temiz" olarak işaretler.</summary>
    private void ResetOcOnExit()
    {
        if (_settings.GpuOcEnabled) GpuOverclock.Apply(0, 0, out _);
        if (!_settings.GpuOcCleanExit)
        {
            _settings.GpuOcCleanExit = true;
            _settings.Save();
        }
    }

    /// <summary>Performans modu: EC'deki MSI Shift modu + uygun Windows güç planı.</summary>
    private void SetPerfMode(PerfMode mode)
    {
        _settings.PerfStartup = mode;
        _settings.Save();
        FanCommand(f => f.SetPerfMode(mode));
        Task.Run(() => PowerPlan.Apply(mode));
    }

    /// <summary>Komutu fan iş parçacığına gönderir. Fan sayfası, bu sıra numarası işlenene kadar seçimi "bekliyor" gösterir.</summary>
    private void FanCommand(Action<FanController> command)
    {
        int seq = ++_commandSeq;
        _dashboard.Fans.PendingSeq = seq;
        _fanCommands.Enqueue((seq, command));
        _wake.Set();
    }

    /// <summary>Kayıtlı fan ayarını EC'ye yeniden yazar (program açılışında ve uykudan dönüşte).</summary>
    private void ApplySavedFanSettings(FanController fan)
    {
        if (_settings.PerfStartup is PerfMode perf) fan.SetPerfMode(perf);

        switch (_settings.FanStartup)
        {
            case FanStartup.Ozel when _settings is { CpuFanTemps: not null, CpuFanSpeeds: not null, GpuFanTemps: not null, GpuFanSpeeds: not null }:
                fan.ApplyCustom(new FanCurve(_settings.CpuFanTemps, _settings.CpuFanSpeeds),
                                new FanCurve(_settings.GpuFanTemps, _settings.GpuFanSpeeds));
                break;
            case FanStartup.Otomatik:
                fan.SetAuto();
                break;
            case FanStartup.SabitDevir:
                fan.SetFixedRpm(_settings.FanFixedRpm);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        FanCommand(ApplySavedFanSettings);
        // Sürücü uykudan dönüşte ofsetleri sıfırlayabilir; açıksa yeniden uygula
        _ui.Post(_ => { if (_settings.GpuOcEnabled) ApplyOc(true, _settings.GpuOcCore, _settings.GpuOcMemory); }, null);
    }

    // ---------------- Sensör okuma (arka planda) ----------------

    /// <summary>
    /// EC (fan) iş parçacığı: komutları gelir gelmez işler ve sonucu hemen arayüze gönderir;
    /// boşta saniyede bir sıcaklık korumasını ve sabit devir ayarını işletir.
    /// </summary>
    private void FanLoop(CancellationToken ct)
    {
        FanController? fan = null;
        try
        {
            fan = new FanController();
            if (fan.State.Available) ApplySavedFanSettings(fan);
            int done = 0;
            _fanState = fan.State;

            var waitHandles = new[] { ct.WaitHandle, _wake };
            while (!ct.IsCancellationRequested)
            {
                bool ran = false;
                while (_fanCommands.TryDequeue(out var command))
                {
                    try { command.run(fan); } catch { }
                    done = command.seq;
                    ran = true;
                }

                var sensors = _lastSensors;
                fan.Tick(sensors.CpuTemp, sensors.GpuTemp, _settings.FanProtection, _settings.FanProtectionTemp);
                _fanState = fan.State with { CommandSeq = done };

                // Komut sonucu sensör döngüsünü beklemeden arayüze gitsin
                if (ran)
                {
                    var state = _fanState;
                    _ui.Post(_ => OnFanState(state), null);
                }
                WaitHandle.WaitAny(waitHandles, 1000);
            }
        }
        catch { }
        finally
        {
            fan?.Dispose();
        }
    }

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

                _lastSensors = snap;
                snap = snap with { Fan = _fanState };

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
            _ui.Post(_ => MessageBox.Show("Sensörler açılamadı:\n" + ex.Message, "Dragon Saj",
                MessageBoxButtons.OK, MessageBoxIcon.Error), null);
        }
        finally
        {
            sensors?.Dispose();
        }
    }

    /// <summary>Fan komutu işlendiğinde, son sensör verisiyle birlikte fan ve performans sayfalarını hemen günceller.</summary>
    private void OnFanState(FanState state)
    {
        var s = _dashboard.View.Data with { Fan = state };
        UpdatePerfMenu(s);
        _dashboard.Fans.SetData(s);
        if (_dashboard.IsPageVisible(_dashboard.Fans)) _dashboard.Fans.Invalidate();
        _dashboard.Perf.Data = s;
        if (_dashboard.IsPageVisible(_dashboard.Perf)) _dashboard.Perf.Invalidate();
    }

    private void UpdatePerfMenu(Snapshot s)
    {
        _perfTurbo.Checked = s.Fan.Perf == PerfMode.Turbo;
        _perfBalanced.Checked = s.Fan.Perf == PerfMode.Dengeli;
        _perfTurbo.Enabled = _perfBalanced.Enabled = s.Fan.Available && s.Fan.Perf != PerfMode.Bilinmiyor;
    }

    private void OnData(Snapshot s)
    {
        // Fan komutları döngüyü erken uyandırabilir; geçmişe saniyede en fazla bir örnek ekle
        if (DateTime.UtcNow - _lastHistory >= TimeSpan.FromMilliseconds(900))
        {
            _lastHistory = DateTime.UtcNow;
            _history.Add(s);
            if (_history.Count > HistorySeconds) _history.RemoveAt(0);
        }

        UpdateTrayIcon(s);
        _overlay.SetData(s);
        UpdatePerfMenu(s);

        var view = _dashboard.View;
        view.Data = s;
        view.History = _history;
        _dashboard.Fans.SetData(s);
        _dashboard.SetRtss(_rtssActive, _rtssInstalled);
        if (_dashboard.IsPageVisible(view)) view.Invalidate();
        if (_dashboard.IsPageVisible(_dashboard.Fans)) _dashboard.Fans.Invalidate();

        var perf = _dashboard.Perf;
        perf.Data = s;
        if (_dashboard.IsPageVisible(perf))
        {
            if (_readCount % 10 == 0) perf.Oc = GpuOverclock.Read(); // sürücü ofseti sıfırlamış olabilir
            perf.Invalidate();
        }

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

        var open = new ToolStripMenuItem("Dragon Saj'ı aç", null, (_, _) => _dashboard.ShowAndActivate());
        open.Font = new Font(open.Font, FontStyle.Bold);

        var panel = new ToolStripMenuItem("Oyun paneli");
        _modeAuto = new ToolStripMenuItem("Otomatik (tam ekran oyunda)", null, (_, _) => SetMode(PanelMode.Otomatik));
        _modeAlways = new ToolStripMenuItem("Her zaman göster", null, (_, _) => SetMode(PanelMode.HerZaman));
        _modeOff = new ToolStripMenuItem("Kapalı", null, (_, _) => SetMode(PanelMode.Kapali));
        panel.DropDownItems.AddRange([_modeAuto, _modeAlways, _modeOff]);
        _modeAuto.Checked = _settings.Mode == PanelMode.Otomatik;
        _modeAlways.Checked = _settings.Mode == PanelMode.HerZaman;
        _modeOff.Checked = _settings.Mode == PanelMode.Kapali;

        var perf = new ToolStripMenuItem("Performans modu");
        _perfTurbo = new ToolStripMenuItem("Turbo", null, (_, _) => SetPerfMode(PerfMode.Turbo));
        _perfBalanced = new ToolStripMenuItem("Dengeli", null, (_, _) => SetPerfMode(PerfMode.Dengeli));
        perf.DropDownItems.AddRange([_perfTurbo, _perfBalanced]);

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
        menu.Items.AddRange([perf, panel, corner, opacity, _autostart]);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());
        return menu;
    }

    // ---------------- Otomatik başlatma (yönetici izniyle, Görev Zamanlayıcı) ----------------

    private static bool IsAutostartEnabled() => RunSchtasks($"/query /tn \"{TaskName}\"") == 0;

    private static int CreateAutostartTask() =>
        RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{Environment.ProcessPath}\\\" --gizli\" /sc onlogon /rl highest /f");

    /// <summary>Eski adla (Sıcaklık Paneli) kurulmuş otomatik başlatma görevini yeni ada ve exe'ye taşır.</summary>
    private static void MigrateAutostart()
    {
        if (RunSchtasks($"/query /tn \"{OldTaskName}\"") != 0) return;
        if (CreateAutostartTask() == 0)
            RunSchtasks($"/delete /tn \"{OldTaskName}\" /f");
    }

    private void ToggleAutostart()
    {
        int rc = IsAutostartEnabled()
            ? RunSchtasks($"/delete /tn \"{TaskName}\" /f")
            : CreateAutostartTask();
        if (rc != 0)
            MessageBox.Show("Otomatik başlatma ayarı değiştirilemedi.", "Dragon Saj", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        ResetOcOnExit();
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
