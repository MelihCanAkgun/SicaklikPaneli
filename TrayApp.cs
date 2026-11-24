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
    // Simge yalnızca görünen değer (rakam, çizgi rengi, boyut) değişince yeniden çizilir
    private string? _iconText;
    private int _iconColor;
    private Size _iconSize;
    private bool _warnedNoCpuTemp;
    private volatile bool _closing;
    private Task? _sensorTask, _fanTask;
    private readonly SessionEndedEventHandler _onSessionEnded;
    private int _backgroundStopped; // 1: arka plan temizliği (döngüler, OC, RTSS) yapıldı
    private bool _exited;
    private int _readCount;
    // Ekran kartı uykudayken uygulanamayan hız aşırtma ayarı; kart uyanınca uygulanır
    private bool _ocAwaitWake;
    private bool _perfWasVisible;
    // Güç planı çağrıları sırayla ve en son istenen modla yapılır (hızlı tıklamada ters sırada bitmesin)
    private readonly object _planLock = new();
    private volatile PerfMode _wantedPlan;
    // Fan verisini gösteren bir pencere açık mı? Fan iş parçacığı bunu FanController.UiVisible'a aktarır
    private volatile bool _fanUiVisible = true;
    private volatile bool _rtssActive;
    private readonly bool _rtssInstalled = RtssOsd.FindExe() != null;

    // Fan komutları arayüzden fan iş parçacığına bu kuyrukla aktarılır (EC'ye tek yerden erişilir).
    // Fan iş parçacığı sensör okumasından ayrıdır; yavaş bir sensör okuması tıklamaları bekletmez.
    private readonly ConcurrentQueue<(int seq, Action<FanController> run)> _fanCommands = new();
    private readonly AutoResetEvent _wake = new(false);
    private int _commandSeq;
    private volatile FanState _fanState = FanState.Unavailable("Fan bilgisi bekleniyor…");
    private volatile Snapshot _lastSensors = Snapshot.Empty;
    // Son başarılı sensör okumasının zamanı (Environment.TickCount64); Volatile ile okunup yazılır
    private long _lastSensorsAt;
    /// <summary>Bundan eski sensör verisiyle fan koruması karar vermez (okuma takıldıysa ya da hata veriyorsa).</summary>
    private const long SensorStaleMs = 5000;
    private DateTime _lastHistory = DateTime.MinValue;

    private ToolStripMenuItem _modeAuto = null!, _modeAlways = null!, _modeOff = null!, _autostart = null!,
                              _perfTurbo = null!, _perfBalanced = null!;
    private readonly Dictionary<Corner, ToolStripMenuItem> _cornerItems = new();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _onSessionEnded = (_, _) => StopBackground();
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
        _uiTimer.Tick += (_, _) =>
        {
            UpdateOverlayVisibility();
            UpdateFanUiVisible();
        };
        _uiTimer.Start();

        // Ömür boyu süren döngüler iş parçacığı havuzunu meşgul etmesin: ayrı (arka plan) iş parçacıkları
        _sensorTask = Task.Factory.StartNew(() => SensorLoop(_cts.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _fanTask = Task.Factory.StartNew(() => FanLoop(_cts.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

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

        fans.ApplyRequested += (cpu, gpu) =>
        {
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
        var (cpuRpm, gpuRpm) = SavedFixedRpm();
        fans.FixedCpuTarget = cpuRpm;
        fans.FixedGpuTarget = gpuRpm;
        fans.FixedRequested += (cpu, gpu) =>
        {
            _settings.FanStartup = FanStartup.SabitDevir;
            _settings.FanFixedCpuRpm = cpu;
            _settings.FanFixedGpuRpm = gpu;
            _settings.Save();
            FanCommand(f => f.SetFixedRpm(cpu, gpu));
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
        // Oturum gerçekten kapanınca (iptal edilmediyse) temizlik yapılır; bu durumda ExitThreadCore çağrılmayabilir
        SystemEvents.SessionEnded += _onSessionEnded;
    }

    private void ApplyOc(bool enabled, int core, int memory)
    {
        if (_closing) return; // kapanışta sıfırlanan ofset yeniden uygulanmasın
        var perf = _dashboard.Perf;
        core = Math.Clamp(core, 0, PerfView.CoreLimit);
        memory = Math.Clamp(memory, 0, PerfView.MemoryLimit);

        bool ok = GpuOverclock.Apply(enabled ? core : 0, enabled ? memory : 0, out string error, out bool asleep);
        if (!ok && asleep)
        {
            // Ekran kartı uykuda (Optimus): tercihi kaybetme, kart uyanınca uygulanacak (bkz. CheckOcAfterWake).
            // Henüz bir şey yazılmadığı için "temiz kapanış" bayrağına dokunulmaz.
            _ocAwaitWake = true;
            _settings.GpuOcEnabled = enabled;
            _settings.GpuOcCore = core;
            _settings.GpuOcMemory = memory;
            _settings.Save();
            perf.Oc = GpuOverclock.Read();
            perf.OcEnabled = enabled;
            perf.Message = enabled ? "Ekran kartı uykuda; hız aşırtma kart uyandığında uygulanacak."
                                   : "Hız aşırtma kapatıldı.";
            perf.Invalidate();
            return;
        }
        _ocAwaitWake = false;
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
        bool reset = true;
        if (_settings.GpuOcEnabled || _ocAwaitWake)
        {
            // Kart uykudaysa yazılamaz ama ofset de etkin değildir; kapanış yine temiz sayılır.
            // Başka bir hatayla sıfırlanamadıysa oturum temiz sayılmaz: sonraki açılışta yeniden uygulanmaz.
            reset = GpuOverclock.Apply(0, 0, out string error, out bool asleep) || asleep;
            if (!reset) HataGunlugu.Write("Kapanışta hız aşırtma sıfırlanamadı: " + error);
        }
        if (reset && !_settings.GpuOcCleanExit)
        {
            _settings.GpuOcCleanExit = true;
            _settings.Save();
        }
    }

    /// <summary>
    /// Kart uykudayken bekletilen hız aşırtma ayarı: periyodik okuma kartın uyandığını ve ofsetin
    /// istenenden farklı olduğunu gösterirse ayar bir kez uygulanır. Kartı uyandırmak için çağrı yapılmaz.
    /// </summary>
    private OcState? CheckOcAfterWake(OcState? oc)
    {
        if (oc == null || !_ocAwaitWake) return oc;
        if (oc.Asleep || !oc.Available) return oc;

        bool on = _settings.GpuOcEnabled;
        int core = on ? Math.Clamp(_settings.GpuOcCore, 0, PerfView.CoreLimit) : 0;
        int memory = on ? Math.Clamp(_settings.GpuOcMemory, 0, PerfView.MemoryLimit) : 0;
        // Sürücü aralığına sığdırılmış hedefle karşılaştır (aksi halde her okumada yeniden uygulanırdı)
        static bool Same(OcClock c, int target) =>
            !c.Editable || c.OffsetMhz == Math.Clamp(target, Math.Min(0, c.MinMhz), Math.Max(0, c.MaxMhz));

        _ocAwaitWake = false;
        if (Same(oc.Core, core) && Same(oc.Memory, memory)) return oc;
        ApplyOc(on, _settings.GpuOcCore, _settings.GpuOcMemory); // perf.Oc'yi de günceller
        return null;
    }

    /// <summary>Performans modu: EC'deki MSI Shift modu + uygun Windows güç planı.</summary>
    private void SetPerfMode(PerfMode mode)
    {
        _settings.PerfStartup = mode;
        _settings.Save();
        FanCommand(f => f.SetPerfMode(mode));
        // Çağrılar kilitle sıralanır ve her biri o an istenen son modu uygular: hızlı Turbo→Dengeli
        // tıklamasında görevler ters sırada çalışsa bile en son seçilen plan etkin kalır.
        _wantedPlan = mode;
        Task.Run(() =>
        {
            lock (_planLock) PowerPlan.Apply(_wantedPlan);
        });
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

        // Kayıtlı özel eğri, hangi modda açılırsa açılsın eğri düzenleyicide görünsün
        FanCurve? cpu = null, gpu = null;
        if (_settings is { CpuFanTemps: not null, CpuFanSpeeds: not null, GpuFanTemps: not null, GpuFanSpeeds: not null })
        {
            cpu = new FanCurve(_settings.CpuFanTemps, _settings.CpuFanSpeeds);
            gpu = new FanCurve(_settings.GpuFanTemps, _settings.GpuFanSpeeds);
            fan.RememberCustom(cpu, gpu);
        }

        switch (_settings.FanStartup)
        {
            case FanStartup.Ozel when cpu != null && gpu != null:
                fan.ApplyCustom(cpu, gpu);
                break;
            case FanStartup.Otomatik:
                fan.SetAuto();
                break;
            case FanStartup.SabitDevir:
                var (cpuRpm, gpuRpm) = SavedFixedRpm();
                fan.SetFixedRpm(cpuRpm, gpuRpm);
                break;
        }
    }

    private (int cpu, int gpu) SavedFixedRpm()
    {
        static int Clamp(int v) => Math.Clamp(v, FanController.FixedMinRpm, FanController.FixedMaxRpm);
        return (Clamp(_settings.FanFixedCpuRpm ?? _settings.FanFixedRpm), Clamp(_settings.FanFixedGpuRpm ?? _settings.FanFixedRpm));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        FanCommand(ApplySavedFanSettings);
        // Sürücü uykudan dönüşte ofsetleri sıfırlayabilir; açıksa yeniden uygula
        PostUi(() => { if (_settings.GpuOcEnabled) ApplyOc(true, _settings.GpuOcCore, _settings.GpuOcMemory); });
    }

    /// <summary>
    /// İşi arayüz iş parçacığına bekletmeden gönderir. Kapanış başladıysa ya da arayüz artık
    /// ileti alamıyorsa (pencereler yok edilmiş) sessizce bırakır; arka plan döngüsü bundan etkilenmez.
    /// </summary>
    private void PostUi(Action action)
    {
        if (_closing) return;
        try
        {
            _ui.Post(static state =>
            {
                var (app, work) = ((TrayApp, Action))state!;
                if (app._closing) return;
                work();
            }, (this, action));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
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
            fan.UiVisible = _fanUiVisible;
            // Kayıtlı ayar, EC bağlantısı her kurulduğunda uygulanır: ilk açılışta, açılışta EC kullanılamayıp
            // sonradan açıldığında ve hata sonrası yeniden bağlanınca (aynı modu yeniden yazmak zararsızdır).
            int appliedGen = 0;
            int done = 0;
            _fanState = fan.State;

            var waitHandles = new[] { ct.WaitHandle, _wake };
            bool loggedError = false;
            while (!ct.IsCancellationRequested)
            {
                // Tek bir turdaki beklenmeyen hata döngüyü (ve fan denetimini) öldürmesin
                try
                {
                    fan.UiVisible = _fanUiVisible;
                    // Önceki turun Tick'i bağlantıyı yeniden kurduysa, bekleyen komutlardan önce kayıtlı ayarı uygula
                    if (fan.State.Available && fan.InitGeneration != appliedGen)
                    {
                        appliedGen = fan.InitGeneration;
                        ApplySavedFanSettings(fan);
                    }

                    bool ran = false;
                    while (_fanCommands.TryDequeue(out var command))
                    {
                        try { command.run(fan); }
                        catch (Exception ex) { HataGunlugu.Write("Fan komutu", ex); }
                        done = command.seq;
                        ran = true;
                    }

                    // Sensör verisi bayatsa (okuma takıldı ya da art arda hata veriyor) sıcaklık bilinmiyor sayılır;
                    // FanController bu durumda koruma kararını değiştirmez
                    var sensors = _lastSensors;
                    bool fresh = Environment.TickCount64 - Volatile.Read(ref _lastSensorsAt) <= SensorStaleMs;
                    fan.Tick(fresh ? sensors.CpuTemp : null, fresh ? sensors.GpuTemp : null,
                             _settings.FanProtection, _settings.FanProtectionTemp);
                    _fanState = fan.State with { CommandSeq = done };

                    // Komut sonucu sensör döngüsünü beklemeden arayüze gitsin
                    if (ran)
                    {
                        var state = _fanState;
                        PostUi(() => OnFanState(state));
                    }
                    loggedError = false;
                }
                catch (Exception ex)
                {
                    if (!loggedError) HataGunlugu.Write("Fan döngüsü", ex);
                    loggedError = true;
                }
                WaitHandle.WaitAny(waitHandles, 1000);
            }
        }
        catch (Exception ex)
        {
            HataGunlugu.Write("Fan döngüsü durdu", ex);
            // Arayüz eski "kullanılabilir" durumu göstermeye devam etmesin
            _fanState = FanState.Unavailable("Fan denetimi durdu: " + ex.Message);
        }
        finally
        {
            try { fan?.Dispose(); }
            catch (Exception ex) { HataGunlugu.Write("Fan denetimi kapatılamadı", ex); }
        }
    }

    private void SensorLoop(CancellationToken ct)
    {
        const int ReadFailureLimit = 10;          // bu kadar ardışık okuma hatasından sonra sensörler yeniden açılır
        const long ReopenMinMs = 5000, ReopenMaxMs = 120000;
        Sensors? sensors = null;
        try
        {
            int tick = 0, readFailures = 0;
            bool loopFailed = false, openReported = false;
            long nextOpen = 0, reopenDelay = ReopenMinMs;
            while (!ct.IsCancellationRequested)
            {
                // Tek bir turdaki beklenmeyen hata (ör. RTSS) sensör döngüsünü öldürmesin
                try
                {
                    // Sensörler açılamadıysa ya da kapatıldıysa artan aralıkla yeniden açmayı dene
                    if (sensors == null && Environment.TickCount64 >= nextOpen)
                    {
                        try
                        {
                            sensors = new Sensors();
                            reopenDelay = ReopenMinMs;
                        }
                        catch (Exception ex)
                        {
                            nextOpen = Environment.TickCount64 + reopenDelay;
                            reopenDelay = Math.Min(reopenDelay * 2, ReopenMaxMs);
                            if (!openReported)
                            {
                                openReported = true;
                                HataGunlugu.Write("Sensörler açılamadı", ex);
                                PostUi(() => MessageBox.Show("Sensörler açılamadı:\n" + ex.Message, "Dragon Saj",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error));
                            }
                        }
                    }

                    Snapshot snap = Snapshot.Empty;
                    if (sensors != null)
                    {
                        try
                        {
                            snap = sensors.Read();
                            readFailures = 0;
                            Volatile.Write(ref _lastSensorsAt, Environment.TickCount64);
                        }
                        catch (Exception ex)
                        {
                            if (readFailures == 0) HataGunlugu.Write("Sensör okuma", ex);
                            if (++readFailures >= ReadFailureLimit)
                            {
                                // Sürekli hata: sensörleri kapatıp yeniden aç (ör. sürücü yeniden yüklendi)
                                HataGunlugu.Write($"Sensör okuma {ReadFailureLimit} kez art arda başarısız; sensörler yeniden açılıyor.");
                                try { sensors.Dispose(); } catch { }
                                sensors = null;
                                readFailures = 0;
                                nextOpen = Environment.TickCount64;
                            }
                        }
                    }

                    _lastSensors = snap;
                    snap = snap with { Fan = _fanState };

                    // Oyun içi gösterge (RTSS): özel tam ekranda da görünür
                    if (_settings.Mode != PanelMode.Kapali)
                    {
                        // RTSS kapalıysa başlat. Bağlıysa zaten çalışıyordur; süreç listesini taramaya gerek yok.
                        if (tick % 30 == 0 && !_rtssActive) RtssOsd.EnsureStarted();
                        _rtssActive = RtssOsd.Update(RtssOsd.Format(snap));
                    }
                    else if (_rtssActive)
                    {
                        RtssOsd.Release();
                        _rtssActive = false;
                    }
                    tick++;

                    PostUi(() => OnData(snap));
                    loopFailed = false;
                }
                catch (Exception ex)
                {
                    if (!loopFailed) HataGunlugu.Write("Sensör döngüsü", ex);
                    loopFailed = true;
                }
                ct.WaitHandle.WaitOne(1000);
            }
        }
        catch (Exception ex)
        {
            HataGunlugu.Write("Sensör döngüsü durdu", ex);
        }
        finally
        {
            try { sensors?.Dispose(); } catch { }
        }
    }

    /// <summary>Fan komutu işlendiğinde, son sensör verisiyle birlikte fan ve performans sayfalarını hemen günceller.</summary>
    private void OnFanState(FanState state)
    {
        var s = _dashboard.View.Data with { Fan = _fanState.CommandSeq >= state.CommandSeq ? _fanState : state };
        UpdatePerfMenu(s);
        _dashboard.Fans.SetData(s);
        if (_dashboard.IsPageVisible(_dashboard.Fans)) _dashboard.Fans.Invalidate();
        if (_dashboard.IsPageVisible(_dashboard.Perf)) _dashboard.Perf.UpdateData(s); // yalnızca değişiklikte çizer
        else _dashboard.Perf.Data = s;
    }

    private void UpdatePerfMenu(Snapshot s)
    {
        _perfTurbo.Checked = s.Fan.Perf == PerfMode.Turbo;
        _perfBalanced.Checked = s.Fan.Perf == PerfMode.Dengeli;
        _perfTurbo.Enabled = _perfBalanced.Enabled = s.Fan.Available && s.Fan.Perf != PerfMode.Bilinmiyor;
    }

    private void OnData(Snapshot s)
    {
        // Sensör okuması sürerken fan durumu değişmiş olabilir; her zaman en yenisini göster
        s = s with { Fan = _fanState };
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
        bool perfVisible = _dashboard.IsPageVisible(perf);
        // Hız aşırtma durumu 10 saniyede bir okunur (sürücü ofseti sıfırlamış olabilir): sayfa görünürken ya da
        // uykudaki kart için bekletilen ayar varken. İkincisinde yalnızca sensörler kartın uyanık olduğunu gösteriyorsa.
        OcState? oc = null;
        // Sayfa yeniden görünür olduğunda (sekme değişimi, pencere açılması) da bir kez okunur: gizliyken yenilenmemişti.
        // Okuma kartı uyandırmaz; kart uykudaysa yalnızca "uykuda" döner.
        bool perfShown = perfVisible && !_perfWasVisible;
        _perfWasVisible = perfVisible;
        if (perfShown || (_readCount % 10 == 0 &&
            (perfVisible || (_ocAwaitWake && (s.GpuTemp.HasValue || s.GpuLoad.HasValue)))))
        {
            oc = GpuOverclock.Read();
            if (oc.Asleep && _settings.GpuOcEnabled) _ocAwaitWake = true; // uyanınca ofset denetlenecek
            oc = CheckOcAfterWake(oc);
        }
        if (perfVisible) perf.UpdateData(s, oc); // yalnızca görünen bir şey değişince yeniden çizer
        else
        {
            perf.Data = s; // gizliyken çizim yok; sayfa açılınca bu veriyle çizilir
            if (oc != null) perf.Oc = oc;
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
        string text = s?.CpuTemp is float t ? Math.Min(t, 99).ToString("0") : "--";
        bool throttle = s is { CpuThrottle: true } or { GpuThrottle: true };
        Color barColor = throttle ? Theme.Warning : Theme.TempColor(s?.CpuTemp);

        // Görünen hiçbir şey değişmediyse yeni simge üretme (GDI tutamacı ve kabuk güncellemesi tasarrufu)
        if (_lastIconHandle != IntPtr.Zero && text == _iconText && barColor.ToArgb() == _iconColor && size == _iconSize)
            return;
        _iconText = text;
        _iconColor = barColor.ToArgb();
        _iconSize = size;

        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            // Büyük beyaz rakamlar simgenin tamamını kaplar, renk alttaki çizgiyle gösterilir
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = size.Width, h = size.Height;
            int bar = Math.Max(2, h / 8);

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
            using var barBrush = new SolidBrush(barColor);
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
        // Panel kapalıyken ön plan penceresini sorgulamaya gerek yok
        Screen? screen = null;
        bool fullscreen = _settings.Mode != PanelMode.Kapali && Native.IsForegroundFullscreen(out screen);
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

    /// <summary>
    /// Fan devri ve yüzdesi yalnızca ana pencerede (Gösterge ve Fan sayfaları) görünür; oyun paneli, RTSS ve
    /// tepsi ipucu göstermez. Pencere kapalı ya da simge durumundayken fan iş parçacığı salt görüntü amaçlı
    /// EC okumalarını seyreltir (koruma ve sabit devir etkilenmez).
    /// </summary>
    private void UpdateFanUiVisible()
    {
        bool visible = _dashboard.Visible && _dashboard.WindowState != FormWindowState.Minimized;
        if (visible == _fanUiVisible) return;
        _fanUiVisible = visible;
        if (visible) _wake.Set(); // pencere açılınca güncel fan durumu hemen okunsun
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

    private static bool IsAutostartEnabled() => TaskExists(TaskName);

    /// <summary>
    /// Görev kayıtlı mı? Görev Zamanlayıcı kök klasördeki her görev için TaskCache\Tree altında bir anahtar tutar;
    /// yönetici olarak bu anahtara bakmak her açılışta schtasks süreci başlatmaktan çok daha ucuzdur.
    /// Anahtar okunamazsa (izin yok vb.) eskisi gibi schtasks'a sorulur.
    /// </summary>
    private static bool TaskExists(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\" + name);
            return key != null;
        }
        catch
        {
            return RunSchtasks($"/query /tn \"{name}\"") == 0;
        }
    }

    private static int CreateAutostartTask() =>
        RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{Environment.ProcessPath}\\\" --gizli\" /sc onlogon /rl highest /f");

    /// <summary>Eski adla (Sıcaklık Paneli) kurulmuş otomatik başlatma görevini yeni ada ve exe'ye taşır.</summary>
    private static void MigrateAutostart()
    {
        if (!TaskExists(OldTaskName)) return;
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

    /// <summary>
    /// Donanımla ilgili temizlik: hız aşırtmayı sıfırlar, döngüleri durdurup bekler (fan denetleyicisi kendi
    /// iş parçacığında kapanır ve korumanın açtığı tam hızı bırakır), RTSS girdisini kaldırır.
    /// Hem program kapanışında hem Windows oturumu biterken (ExitThreadCore çağrılmayabilir) bir kez çalışır.
    /// Arayüz nesnelerine dokunmaz.
    /// </summary>
    private void StopBackground()
    {
        if (Interlocked.Exchange(ref _backgroundStopped, 1) != 0) return;
        _closing = true; // bundan sonra arka plandan gelen arayüz işleri yapılmaz

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnded -= _onSessionEnded;
        try { ResetOcOnExit(); }
        catch (Exception ex) { HataGunlugu.Write("Kapanışta hız aşırtma", ex); }

        // Döngülerin bitmesini kısa süre bekle: fan denetleyicisi (korumanın açtığı tam hızı bırakır)
        // ve sensörler süreç bitmeden düzgün kapansın, RTSS girdisi de silindikten sonra yeniden yazılmasın.
        _cts.Cancel();
        try
        {
            var loops = new[] { _sensorTask, _fanTask }.OfType<Task>().ToArray();
            if (!Task.WaitAll(loops, TimeSpan.FromSeconds(3)))
                HataGunlugu.Write("Kapanışta arka plan döngüleri zamanında bitmedi.");
        }
        catch (Exception ex) { HataGunlugu.Write("Kapanış", ex); }

        // Girdiyi siler ve sonradan gelebilecek güncellemeleri yok sayar (döngü zamanında bitmediyse bile)
        RtssOsd.Shutdown();
    }

    protected override void ExitThreadCore()
    {
        if (_exited) return; // ikinci çağrı (ör. Windows kapanırken): her şey zaten bırakıldı
        _exited = true;

        _uiTimer.Stop();
        StopBackground();

        Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
        _hotkeys.DestroyHandle();
        _uiTimer.Dispose();
        var menu = _tray.ContextMenuStrip;
        _tray.Visible = false;
        _tray.Dispose();
        menu?.Dispose();
        _overlay.Close();
        _dashboard.Dispose();
        if (_lastIconHandle != IntPtr.Zero) Native.DestroyIcon(_lastIconHandle);
        _lastIconHandle = IntPtr.Zero;
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
