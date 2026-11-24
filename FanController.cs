using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware.Motherboard.Lpc.EC;

namespace SicaklikPaneli;

/// <summary>
/// MSI dizüstülerin gömülü denetleyicisi (EC) üzerinden fanları okur ve yönetir.
/// Yalnızca adres düzeni doğrulanmış bellenim ailelerinde yazma yapar.
/// Tüm çağrılar tek bir iş parçacığından (fan döngüsü) ve eşzamanlı (async olmadan) yapılmalıdır:
/// EC kilidi (adlandırılmış mutex) iş parçacığına bağlıdır. Yalnızca <see cref="UiVisible"/> başka
/// iş parçacığından ayarlanabilir.
/// </summary>
public sealed class FanController : IDisposable
{
    // EC adresleri (MSI "G1_1" bellenim ailesi)
    private const byte AddrFirmware = 0xA0, AddrFanMode = 0xF4, AddrBoost = 0x98,
                       AddrCpuTemps = 0x6A, AddrCpuSpeeds = 0x72, AddrGpuTemps = 0x82, AddrGpuSpeeds = 0x8A,
                       AddrCpuPercent = 0x71, AddrGpuPercent = 0x89, AddrGpuRpm = 0xCA, AddrCpuRpm = 0xCC;
    private const byte AddrShift = 0xF2, ShiftTurbo = 0xC4, ShiftSport = 0xC0, ShiftComfort = 0xC1, ShiftEco = 0xC2;
    private const byte ModeMask = 0xC0, ModeAuto = 0x00, ModeBasic = 0x40, ModeAdvanced = 0x80, BoostBit = 0x80;

    /// <summary>Aynı adres düzenini kullanan bellenim aileleri.</summary>
    private static readonly string[] SupportedFirmware =
    {
        "16U7EMS1", "17E7EMS1", "17E8EMS1", "17F2EMS1", "17F3EMS1",
        "17F3EMS2", "17F4EMS1", "17F4EMS2", "17F5EMS1", "17F6EMS1"
    };

    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SicaklikPaneli", "fan_fabrika.json");

    /// <summary>LibreHardwareMonitor ve diğer izleme araçlarının EC erişiminde kullandığı ortak kilit.</summary>
    private const string EcMutexName = @"Global\Access_EC";
    private const int CommandLockMs = 1000, TickLockMs = 250, ReadAttempts = 5;
    /// <summary>Denetim (dış değişiklik algılama, sabit devir düzeltmesi) aralığı.</summary>
    private const long CheckIntervalMs = 3000;
    /// <summary>Arayüz kapalıyken salt görüntü amaçlı durum okuması aralığı.</summary>
    private const long HiddenRefreshMs = 3000;
    /// <summary>EC açılamadığında yeniden deneme aralığı.</summary>
    private const long ReopenIntervalMs = 10000;
    /// <summary>Bu kadar ardışık başarısız turdan sonra kullanıcıya bildirilir ve EC yeniden açılır.</summary>
    private const int FailureLimit = 5;

    // LibreHardwareMonitor'ın EC okuyucusu: art arda 20'den fazla bekleme hatasından sonra veri hazır
    // bayrağını beklemeyi kalıcı olarak bırakıyor (bayat okumalar). Ara sıra sayacı sıfırlıyoruz.
    private const int WaitFailureLimit = 20;
    private static readonly FieldInfo? WaitFailuresField =
        typeof(WindowsEmbeddedControllerIO).GetField("_waitReadFailures", BindingFlags.Instance | BindingFlags.NonPublic);

    private delegate bool TryReadOp(byte register, out byte value);

    private WindowsEmbeddedControllerIO? _ec;
    private Func<byte, byte>? _read;
    private TryReadOp? _tryRead;
    private Action<byte, byte>? _write;
    private Mutex? _ecMutex;
    private long _lastWaitReset = long.MinValue / 2;

    // Bir tur (kilit alındığı süre) içinde doğrulanmış okumaların önbelleği; aynı bayt bir turda bir kez okunur
    private readonly int[] _cacheGen = new int[256];
    private readonly byte[] _cacheVal = new byte[256];
    private int _gen = 1;

    // Bağlantı durumu
    private bool _initialized, _retryOpen;
    private long _nextOpenTry;
    private int _failures, _busy;
    private volatile bool _uiVisible = true;

    private FanState _state = FanState.Unavailable("Fan denetimi başlatılıyor…");
    private bool _manualBoost, _autoBoost;
    private long _lastHot = long.MinValue / 2;
    private int _revision;
    private long _lastRefresh = long.MinValue / 2;

    // Sabit devir: hedef RPM'e ulaşmak için eğrinin hız değerleri birkaç saniyede bir düzeltilir
    public const int FixedMinRpm = 1500, FixedMaxRpm = 6200, FixedSafetyTemp = 88, FixedMinSpeed = 20;
    /// <summary>Sabit devir eğrisinin eşikleri; son eşiğin üstünde fanlar her durumda tam hıza çıkar.</summary>
    private static readonly int[] FixedTemps = { 60, 65, 70, 75, 80, FixedSafetyTemp };
    private int _fixedCpuRpm, _fixedGpuRpm, _fixedCpuSpeed, _fixedGpuSpeed;
    private FanCurve? _fixedCpuCurve, _fixedGpuCurve;

    /// <summary>
    /// Programın istediği mod. Ekranda bu gösterilir; EC'nin anlık okuması değil (MSI Center ve Windows'un
    /// EC sürücüsü de EC'yi yokladığı için tek tek okumalar yanıltıcı olabilir).
    /// </summary>
    private FanSelect _intent = FanSelect.Bilinmiyor;
    /// <summary>Son uygulanan özel eğri; sabit devir ya da otomatik moddayken de ekranda bu gösterilir.</summary>
    private FanCurve? _customCpu, _customGpu;
    /// <summary>Doğrulanıp EC'ye yazılmış özel eğri; yalnızca bu eğri yeniden uygulanır.</summary>
    private FanCurve? _appliedCpu, _appliedGpu;
    private int _mismatch;
    private long _lastCheck = long.MinValue / 2;

    public FanState State => _state;

    /// <summary>
    /// Fan durumunu gösteren bir arayüz (pencere, panel) açık mı? Varsayılan true (her saniye tam durum okunur).
    /// false iken salt görüntü amaçlı okumalar (yüzde, devir, performans modu) birkaç saniyede bire seyreltilir;
    /// sıcaklık koruması, dış değişiklik denetimi ve sabit devir düzeltmesi etkilenmez. Her iş parçacığından ayarlanabilir.
    /// </summary>
    public bool UiVisible { get => _uiVisible; set => _uiVisible = value; }

    /// <summary>
    /// EC bağlantısı her başarıyla kurulduğunda (ilk açılış ya da hata/uyku sonrası yeniden bağlanma) artar.
    /// Değiştiğinde kayıtlı fan ayarının yeniden uygulanması uygundur.
    /// </summary>
    public int InitGeneration { get; private set; }

    public FanController()
    {
        _ecMutex = OpenEcMutex();
        TryOpen();
    }

    /// <summary>EC'yi açar, bellenimi doğrular; ilk seferde eğrileri ve fabrika yedeğini de yükler.</summary>
    private void TryOpen()
    {
        try
        {
            // Kilidi önce biz alıyoruz; böylece kütüphanenin kurucusundaki kısa (10 ms) bekleme
            // başka bir program kilidi o an tutuyor diye başarısız olmaz.
            if (!Acquire(CommandLockMs)) throw new TimeoutException("EC başka bir program tarafından kullanılıyor.");
            _ec = new WindowsEmbeddedControllerIO();
            // Kurucu kilidi kendisi de alıp Dispose'a kadar tutuyor; kalıcı tutmamak için tümünü bırakıp
            // yalnızca bu işlem süresince yeniden alıyoruz.
            ReleaseAll();
            if (!Acquire(CommandLockMs)) throw new TimeoutException("EC başka bir program tarafından kullanılıyor.");
            NewBatch();

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(WindowsEmbeddedControllerIO);
            _read = type.GetMethod("ReadByte", flags)!.CreateDelegate<Func<byte, byte>>(_ec);
            _write = type.GetMethod("WriteByte", flags)!.CreateDelegate<Action<byte, byte>>(_ec);
            // Başarısız okumayı 0 sanmamak için mümkünse sonucu bildiren iç işlem kullanılır
            try { _tryRead = type.GetMethod("ReadByteOp", flags)?.CreateDelegate<TryReadOp>(_ec); }
            catch { _tryRead = null; }

            string fw = ReadFirmware();
            if (!IsSupported(fw))
            {
                Fail($"Bu cihazın fan denetimi desteklenmiyor (bellenim: {(fw.Length > 0 ? fw : "okunamadı")}).", permanent: true);
                return;
            }

            string? message = null;
            if (!_initialized)
            {
                var cpu = ReadCurve(AddrCpuTemps, AddrCpuSpeeds);
                var gpu = ReadCurve(AddrGpuTemps, AddrGpuSpeeds);
                if (!LooksSane(cpu) || !LooksSane(gpu))
                {
                    Fail("EC'deki fan tablosu beklenen biçimde değil; güvenlik için fan denetimi kapalı.", permanent: true);
                    return;
                }

                var (factoryCpu, factoryGpu) = LoadOrCreateBackup(cpu, gpu, fw);
                _customCpu = cpu.Copy();
                _customGpu = gpu.Copy();
                _state = new FanState
                {
                    Available = true, Firmware = fw, Cpu = cpu, Gpu = gpu,
                    FactoryCpu = factoryCpu, FactoryGpu = factoryGpu, Revision = ++_revision
                };
                _initialized = true;
            }
            else
            {
                message = "EC bağlantısı yeniden kuruldu.";
                _mismatch = 0;
            }

            _retryOpen = false;
            _failures = _busy = 0;
            InitGeneration++;
            RefreshStatus(verify: true, message);
        }
        catch (Exception ex)
        {
            Fail((_initialized ? "EC bağlantısı yeniden kurulamadı: " : "EC'ye erişilemedi: ") + ex.Message + " Yeniden denenecek.",
                 permanent: false);
        }
        finally
        {
            // Bu iş parçacığı işlem dışında EC kilidini hiç tutmamalı
            ReleaseAll();
        }
    }

    private static bool IsSupported(string fw)
    {
        foreach (var prefix in SupportedFirmware)
            if (fw.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>EC nesnesini kapatır. Kalıcı değilse <see cref="Tick"/> belirli aralıkla yeniden açmayı dener.</summary>
    private void Fail(string message, bool permanent)
    {
        _state = !permanent && _initialized ? _state with { Message = message } : FanState.Unavailable(message);
        if (permanent) _initialized = false;
        CloseEc();
        _retryOpen = !permanent;
        _nextOpenTry = Environment.TickCount64 + ReopenIntervalMs;
    }

    private void CloseEc()
    {
        // Kütüphane Dispose'da kendi kilit sayımını bırakır; hata vermesin diye kilidi tutarken çağrılır
        if (_ec == null) return;
        bool held = Acquire(CommandLockMs);
        try { _ec.Dispose(); } catch { }
        _ec = null;
        _read = null;
        _write = null;
        _tryRead = null;
        if (held) ReleaseAll();
    }

    // ---------------- EC kilidi ----------------

    private static Mutex? OpenEcMutex()
    {
        try
        {
            if (Mutex.TryOpenExisting(EcMutexName, out var existing)) return existing;
            // Kütüphane gibi herkesin açabileceği izinlerle oluştur (yönetici olmayan araçlar da kullanabilsin)
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            return MutexAcl.Create(false, EcMutexName, out _, security);
        }
        catch
        {
            try { return new Mutex(false, EcMutexName); } catch { return null; }
        }
    }

    private bool Acquire(int timeoutMs)
    {
        if (_ecMutex == null) return true;
        try { return _ecMutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; } // önceki sahibi kapanmış; kilit artık bizde
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Bu iş parçacığının tuttuğu tüm EC kilidi sayımlarını bırakır (kütüphaneninkiler dahil).</summary>
    private void ReleaseAll()
    {
        if (_ecMutex == null) return;
        for (int i = 0; i < 16; i++)
        {
            try { _ecMutex.ReleaseMutex(); }
            catch { return; } // artık sahibi değiliz
        }
    }

    /// <summary>EC'ye yapılacak bir dizi işlem (tur) için kilidi alır; <c>using</c> ile bırakılır.</summary>
    private EcScope EnterEc(int timeoutMs)
    {
        if (!Acquire(timeoutMs)) throw new TimeoutException("EC başka bir program tarafından kullanılıyor.");
        NewBatch();
        ResetStuckWait();
        return new EcScope(_ecMutex);
    }

    private readonly struct EcScope : IDisposable
    {
        private readonly Mutex? _mutex;
        public EcScope(Mutex? mutex) => _mutex = mutex;
        public void Dispose()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>Yeni tur: önceki turun okumaları artık geçerli sayılmaz.</summary>
    private void NewBatch()
    {
        if (++_gen <= 0) { Array.Clear(_cacheGen); _gen = 1; }
    }

    /// <summary>
    /// Okuyucu "bekleme" kipinden kalıcı olarak çıkmışsa (bayat değer okuma riski) sayacı sıfırlar.
    /// EC hiç yanıt vermiyorsa sürekli yavaşlamasın diye en fazla 30 saniyede bir denenir.
    /// </summary>
    private void ResetStuckWait()
    {
        if (_ec == null || WaitFailuresField == null) return;
        long now = Environment.TickCount64;
        if (now - _lastWaitReset < 30000) return;
        _lastWaitReset = now;
        try
        {
            if (WaitFailuresField.GetValue(_ec) is int n && n > WaitFailureLimit) WaitFailuresField.SetValue(_ec, 0);
        }
        catch { }
    }

    // ---------------- Düşük seviye: doğrulamalı okuma / yazma ----------------

    /// <summary>Tek okuma. Okunamazsa 0 döndürmek yerine hata verir (oku-değiştir-yaz bitleri bozmasın).</summary>
    private byte ReadRaw(byte addr)
    {
        var tryRead = _tryRead;
        if (tryRead == null)
            return (_read ?? throw new IOException("EC kapalı."))(addr);
        for (int i = 0; i < ReadAttempts; i++)
            if (tryRead(addr, out byte value)) return value;
        throw new IOException($"EC yanıt vermedi (0x{addr:X2}).");
    }

    /// <summary>
    /// Doğrulanmış okuma: başka yazılımlar da EC'yi yokladığı için arka arkaya iki aynı okuma beklenir.
    /// Sonuç tur boyunca önbellekte tutulur (karar verilen ve yazılan değerler için kullanılır).
    /// </summary>
    private byte Read(byte addr)
    {
        if (_cacheGen[addr] == _gen) return _cacheVal[addr];
        byte prev = ReadRaw(addr);
        for (int i = 0; i < 4; i++)
        {
            byte cur = ReadRaw(addr);
            if (cur == prev) break;
            prev = cur;
        }
        _cacheVal[addr] = prev;
        _cacheGen[addr] = _gen;
        return prev;
    }

    /// <summary>Salt görüntü amaçlı tek okuma (tur içinde doğrulanmış değer varsa o kullanılır).</summary>
    private byte ReadOnce(byte addr) => _cacheGen[addr] == _gen ? _cacheVal[addr] : ReadRaw(addr);

    /// <summary>Karşılaştırma için okuma: tek okuma beklenenle aynıysa yeter; farklıysa doğrulanmış okumayla kesinleşir.</summary>
    private bool Matches(byte addr, int expected) => ReadOnce(addr) == expected || Read(addr) == expected;

    /// <summary>Yazar ve geri okuyarak doğrular.</summary>
    private bool Write(byte addr, byte value)
    {
        var write = _write ?? throw new IOException("EC kapalı.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Read(addr) == value) return true;
            write(addr, value);
            _cacheGen[addr] = 0; // geri okuma taze olsun
        }
        return Read(addr) == value;
    }

    private string ReadFirmware()
    {
        var sb = new StringBuilder(12);
        for (int i = 0; i < 12; i++)
        {
            char c = (char)Read((byte)(AddrFirmware + i));
            if (c is < ' ' or > '~') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private FanCurve ReadCurve(byte tempAddr, byte speedAddr)
    {
        var temps = new int[6];
        var speeds = new int[7];
        for (int i = 0; i < 6; i++) temps[i] = Read((byte)(tempAddr + i));
        for (int i = 0; i < 7; i++) speeds[i] = Read((byte)(speedAddr + i));
        return new FanCurve(temps, speeds);
    }

    /// <summary>EC'deki eğri verilenle aynı mı? İlk farkta durur, bellek ayırmaz.</summary>
    private bool CurveMatches(byte tempAddr, byte speedAddr, FanCurve c)
    {
        for (int i = 0; i < 6; i++) if (!Matches((byte)(tempAddr + i), c.Temps[i])) return false;
        for (int i = 0; i < 7; i++) if (!Matches((byte)(speedAddr + i), c.Speeds[i])) return false;
        return true;
    }

    /// <summary>Okunan tablo gerçekten bir fan eğrisine benziyor mu? (Yanlış adres düzenine karşı önlem)</summary>
    private static bool LooksSane(FanCurve c)
    {
        if (c.Temps is not { Length: 6 } || c.Speeds is not { Length: 7 }) return false;
        for (int i = 0; i < 6; i++)
        {
            if (c.Temps[i] is < 30 or > 105) return false;
            if (i > 0 && c.Temps[i] <= c.Temps[i - 1]) return false;
        }
        foreach (int s in c.Speeds) if (s is < 0 or > FanCurve.MaxSpeed) return false;
        return true;
    }

    private bool WriteCurve(byte tempAddr, byte speedAddr, FanCurve c)
    {
        bool ok = true;
        for (int i = 0; i < 6; i++) ok &= Write((byte)(tempAddr + i), (byte)c.Temps[i]);
        for (int i = 0; i < 7; i++) ok &= Write((byte)(speedAddr + i), (byte)c.Speeds[i]);
        return ok;
    }

    // ---------------- Fabrika yedeği ----------------

    internal sealed record Backup(int[] CpuTemps, int[] CpuSpeeds, int[] GpuTemps, int[] GpuSpeeds, string Firmware, DateTime Date);

    /// <summary>İlk çalıştırmada EC'deki özgün eğriyi dosyaya kaydeder; sonra hep o dosya "fabrika ayarı"dır.</summary>
    private static (FanCurve cpu, FanCurve gpu) LoadOrCreateBackup(FanCurve cpu, FanCurve gpu, string firmware)
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                var b = JsonSerializer.Deserialize(File.ReadAllText(BackupPath), BackupJsonContext.Default.Backup);
                if (b != null)
                {
                    var bc = new FanCurve(b.CpuTemps, b.CpuSpeeds);
                    var bg = new FanCurve(b.GpuTemps, b.GpuSpeeds);
                    if (LooksSane(bc) && LooksSane(bg))
                        return (bc, bg);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            var backup = new Backup(cpu.Temps, cpu.Speeds, gpu.Temps, gpu.Speeds, firmware, DateTime.Now);
            // Yarım kalmış yazma yedeği bozmasın: önce geçici dosyaya yaz, sonra yerine taşı
            string tmp = BackupPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(backup, BackupJsonContext.Default.Backup));
            File.Move(tmp, BackupPath, overwrite: true);
        }
        catch { }
        return (cpu.Copy(), gpu.Copy());
    }

    // ---------------- Komutlar ----------------

    /// <summary>Kayıtlı özel eğriyi hatırlar (EC'ye yazmaz); eğri düzenleyici bunu gösterir.</summary>
    public void RememberCustom(FanCurve cpu, FanCurve gpu)
    {
        if (_ec == null) return;
        _customCpu = cpu.Copy();
        _customGpu = gpu.Copy();
        _state = _state with { Cpu = _customCpu, Gpu = _customGpu, Revision = ++_revision };
    }

    /// <summary>Özel eğriyi EC'ye yazar ve "gelişmiş" fan moduna geçer.</summary>
    public bool ApplyCustom(FanCurve cpu, FanCurve gpu)
    {
        if (_ec == null) return false;
        // Doğrulanan ile yazılan aynı olsun diye önce kopyala
        cpu = cpu.Copy();
        gpu = gpu.Copy();
        // Fabrika eğrisi olduğu gibi geri yüklenebilir; diğer her eğri güvenlik kurallarından geçmeli
        string err = "";
        bool cpuOk = cpu.SameAs(_state.FactoryCpu) || cpu.IsValid(out err);
        bool gpuOk = cpuOk && (gpu.SameAs(_state.FactoryGpu) || gpu.IsValid(out err));
        if (!cpuOk || !gpuOk)
        {
            _state = _state with { Message = err };
            return false;
        }
        try
        {
            using var ec = EnterEc(CommandLockMs);
            _intent = FanSelect.Ozel;
            _customCpu = _appliedCpu = cpu;
            _customGpu = _appliedGpu = gpu;
            _manualBoost = false;
            bool ok = WriteIntent() & SetBoostBit(_autoBoost);
            // Ekranda EC'den geri okunan eğri gösterilsin (yazılamayan değer varsa görünsün)
            _state = _state with
            {
                Cpu = ReadCurve(AddrCpuTemps, AddrCpuSpeeds),
                Gpu = ReadCurve(AddrGpuTemps, AddrGpuSpeeds),
                Revision = ++_revision
            };
            RefreshStatus(verify: true, ok ? "Eğri uygulandı." : "Bazı değerler yazılamadı; EC'deki güncel eğri gösteriliyor.");
            return ok;
        }
        catch (Exception ex) { _state = _state with { Message = "Yazma hatası: " + ex.Message }; return false; }
    }

    /// <summary>Fan yönetimini EC'nin kendi otomatik moduna bırakır.</summary>
    public bool SetAuto()
    {
        if (_ec == null) return false;
        try
        {
            using var ec = EnterEc(CommandLockMs);
            _intent = FanSelect.Otomatik;
            _manualBoost = false;
            bool ok = WriteIntent() & SetBoostBit(_autoBoost);
            RefreshStatus(verify: true, ok ? "Otomatik moda geçildi." : "Mod değiştirilemedi.");
            return ok;
        }
        catch (Exception ex) { _state = _state with { Message = "Yazma hatası: " + ex.Message }; return false; }
    }

    /// <summary>
    /// İşlemci ve ekran kartı fanını ayrı ayrı verilen devirde tutar. Önce tahmini bir hızla düz bir eğri
    /// yazılır, sonra <see cref="Tick"/> ölçülen devre bakarak hızı hedefe doğru düzeltir.
    /// </summary>
    public bool SetFixedRpm(int cpuRpm, int gpuRpm)
    {
        if (_ec == null) return false;
        try
        {
            using var ec = EnterEc(CommandLockMs);
            bool wasFixed = _intent == FanSelect.Sabit;
            cpuRpm = Math.Clamp(cpuRpm, FixedMinRpm, FixedMaxRpm);
            gpuRpm = Math.Clamp(gpuRpm, FixedMinRpm, FixedMaxRpm);
            // Hedefi değişmeyen fanın hızına dokunma (zaten ayarlanmış olabilir)
            if (!wasFixed || cpuRpm != _fixedCpuRpm)
                _fixedCpuSpeed = GuessSpeed(cpuRpm, _state.CpuRpm, _state.CpuPercent, wasFixed ? _fixedCpuSpeed : null);
            if (!wasFixed || gpuRpm != _fixedGpuRpm)
                _fixedGpuSpeed = GuessSpeed(gpuRpm, _state.GpuRpm, _state.GpuPercent, wasFixed ? _fixedGpuSpeed : null);
            _fixedCpuRpm = cpuRpm;
            _fixedGpuRpm = gpuRpm;
            _intent = FanSelect.Sabit;
            _manualBoost = false;
            bool ok = WriteIntent() & SetBoostBit(_autoBoost);
            _lastCheck = Environment.TickCount64;
            RefreshStatus(verify: true, ok ? $"Sabit devir: işlemci {cpuRpm}, ekran kartı {gpuRpm} RPM hedefleniyor…" : "Sabit devir ayarlanamadı.");
            return ok;
        }
        catch (Exception ex) { _state = _state with { Message = "Yazma hatası: " + ex.Message }; return false; }
    }

    /// <summary>Tam hız (Cooler Boost) aç/kapat.</summary>
    public bool SetManualBoost(bool on)
    {
        if (_ec == null) return false;
        try
        {
            using var ec = EnterEc(CommandLockMs);
            _manualBoost = on;
            bool ok = SetBoostBit(on || _autoBoost);
            RefreshStatus(verify: true, ok ? (on ? "Tam hız açıldı." : "Tam hız kapatıldı.") : "Tam hız ayarı değiştirilemedi.");
            return ok;
        }
        catch (Exception ex) { _state = _state with { Message = "Yazma hatası: " + ex.Message }; return false; }
    }

    /// <summary>Performans modunu (MSI Shift) değiştirir. Yalnızca kayıt bilinen bir değerdeyse yazar.</summary>
    public bool SetPerfMode(PerfMode mode)
    {
        if (_ec == null) return false;
        try
        {
            byte? value = mode switch { PerfMode.Turbo => ShiftTurbo, PerfMode.Dengeli => ShiftComfort, _ => null };
            using var ec = EnterEc(CommandLockMs);
            if (value == null || ToPerfMode(Read(AddrShift)) == PerfMode.Bilinmiyor)
            {
                _state = _state with { Message = "Performans modu bu cihazda değiştirilemiyor." };
                return false;
            }
            bool ok = Write(AddrShift, value.Value);
            RefreshStatus(verify: true, ok ? $"Performans modu: {mode}." : "Performans modu değiştirilemedi.");
            return ok;
        }
        catch (Exception ex) { _state = _state with { Message = "Yazma hatası: " + ex.Message }; return false; }
    }

    private static PerfMode ToPerfMode(byte raw) => raw switch
    {
        ShiftTurbo => PerfMode.Turbo,
        ShiftSport => PerfMode.Sport,
        ShiftComfort => PerfMode.Dengeli,
        ShiftEco => PerfMode.Eco,
        _ => PerfMode.Bilinmiyor
    };

    // ---------------- İstenen durumu EC'ye yazma / denetleme ----------------

    /// <summary>İstenen moda göre EC'de olması gereken eğriler (otomatik modda null).</summary>
    private (FanCurve cpu, FanCurve gpu)? ExpectedCurves() => _intent switch
    {
        FanSelect.Ozel when _appliedCpu != null && _appliedGpu != null => (_appliedCpu, _appliedGpu),
        FanSelect.Sabit => (FixedCurve(ref _fixedCpuCurve, _fixedCpuSpeed), FixedCurve(ref _fixedGpuCurve, _fixedGpuSpeed)),
        _ => null
    };

    private byte ExpectedMode => _intent == FanSelect.Otomatik ? ModeAuto : ModeAdvanced;

    /// <summary>İstenen eğrileri ve modu EC'ye yazar.</summary>
    private bool WriteIntent()
    {
        bool ok = true;
        if (ExpectedCurves() is var (cpu, gpu))
            ok = WriteCurve(AddrCpuTemps, AddrCpuSpeeds, cpu) & WriteCurve(AddrGpuTemps, AddrGpuSpeeds, gpu);
        return ok & SetMode(ExpectedMode);
    }

    /// <summary>
    /// EC hâlâ istenen durumda mı? Başka bir yazılım (ör. MSI Center) değiştirdiyse yeniden yazar.
    /// Tek bir hatalı okumaya kanmamak için art arda iki denetimde de farklı görünmesi beklenir.
    /// </summary>
    private void Enforce()
    {
        if (_intent == FanSelect.Bilinmiyor) return;
        bool same = (Read(AddrFanMode) & ModeMask) == ExpectedMode;
        if (same && ExpectedCurves() is var (cpu, gpu))
            same = CurveMatches(AddrCpuTemps, AddrCpuSpeeds, cpu) && CurveMatches(AddrGpuTemps, AddrGpuSpeeds, gpu);

        if (same) { _mismatch = 0; return; }
        if (++_mismatch < 2) return;
        _mismatch = 0;
        if (WriteIntent())
            _state = _state with { Message = "Fan ayarı dışarıdan değiştirilmişti (ör. MSI Center); yeniden uygulandı." };
    }

    /// <summary>Sabit devir eğrisi; hız değişmedikçe aynı nesne kullanılır (her denetimde yeni dizi ayrılmaz).</summary>
    private static FanCurve FixedCurve(ref FanCurve? cache, int speed)
    {
        if (cache == null || cache.Speeds[0] != speed)
            cache = new((int[])FixedTemps.Clone(), new[] { speed, speed, speed, speed, speed, speed, FanCurve.MaxSpeed });
        return cache;
    }

    /// <summary>Hedef devir için başlangıç hızı: fan dönüyorsa ölçülen hız/devir oranından, dönmüyorsa kaba tahminle.</summary>
    private static int GuessSpeed(int target, int rpm, int percent, int? current)
    {
        int speed = rpm > 300 && percent > 0 ? (int)Math.Round(percent * (double)target / rpm)
                  : current ?? (int)Math.Round(target / 41.3); // tam hız (150) ≈ 6200 RPM
        return Math.Clamp(speed, FixedMinSpeed, FanCurve.MaxSpeed);
    }

    /// <summary>Sabit devir modunda ölçülen devre göre hız değerini hedefe yaklaştırır.</summary>
    private void Correct(ref int speed, int rpm, int percent, float? temp, byte speedAddr, int target)
    {
        if (temp >= FixedSafetyTemp) return; // güvenlik kademesindeyken fan zaten tam hızda
        // EC fanı bizim hızımızın üstünde tam hızda döndürüyorsa (EC kendi sıcaklığıyla güvenlik
        // kademesine geçmiş) ölçülen devir hedefimizi yansıtmaz; düzeltme yapma
        if (percent >= FanCurve.MaxSpeed && speed < FanCurve.MaxSpeed) return;
        int next;
        if (rpm < 300) next = speed + 10; // fan durmuş ya da yeni kalkıyor
        else
        {
            double ratio = (double)target / rpm;
            if (Math.Abs(ratio - 1) < 0.04) return; // yeterince yakın
            next = (int)Math.Round(speed * (1 + 0.6 * (ratio - 1))); // ani salınım olmasın diye kısmi düzeltme
            if (next == speed) next += ratio > 1 ? 1 : -1;
        }
        next = Math.Clamp(next, FixedMinSpeed, FanCurve.MaxSpeed);
        if (next == speed) return;
        // Sıcaklık bilinmiyorsa güvenli tarafta kal: hızı yalnızca artır
        if (temp == null && next < speed) return;
        speed = next;
        for (int i = 0; i < 6; i++) Write((byte)(speedAddr + i), (byte)next);
    }

    private bool SetMode(byte mode) => Write(AddrFanMode, (byte)((Read(AddrFanMode) & ~ModeMask) | mode));

    private bool SetBoostBit(bool on)
    {
        byte cur = Read(AddrBoost);
        return Write(AddrBoost, (byte)(on ? cur | BoostBit : cur & ~BoostBit));
    }

    // ---------------- Her turda: sıcaklık koruması + durum ----------------

    /// <summary>
    /// Sıcaklık korumasını işletir, EC'nin istenen durumda kaldığını denetler, sabit devir modunda
    /// hızı düzeltir ve fan durumunu günceller. EC bağlantısı koptuysa belirli aralıkla yeniden kurar.
    /// </summary>
    public void Tick(float? cpuTemp, float? gpuTemp, bool protectionEnabled, int protectionTemp)
    {
        long now = Environment.TickCount64;
        if (_ec == null)
        {
            if (!_retryOpen || now < _nextOpenTry) return;
            TryOpen();
            if (_ec == null) return;
        }
        try
        {
            using var ec = EnterEc(TickLockMs);

            // Sıcaklık hiç bilinmiyorsa (sensör hatası) koruma kararı değiştirilmez
            float? hottest = cpuTemp is float c ? (gpuTemp is float g ? Math.Max(c, g) : c) : gpuTemp;
            bool want = _autoBoost;
            if (!protectionEnabled) want = false;
            else if (hottest is float h)
            {
                if (h >= protectionTemp) { want = true; _lastHot = now; }
                else if (_autoBoost && h <= protectionTemp - 8 && now - _lastHot > 15000) want = false;
            }

            // Yazılamazsa durum değişmiş sayılmaz; bir sonraki turda yeniden denenir
            if (want != _autoBoost && SetBoostBit(_manualBoost || want))
                _autoBoost = want;

            bool checkDue = now - _lastCheck >= CheckIntervalMs;
            bool correcting = checkDue && _intent == FanSelect.Sabit;
            // Durum okuması; düzeltme bu okumayı (devir, yüzde, tam hız) kullanacağı için o turda doğrulanır
            if (correcting || _uiVisible || now - _lastRefresh >= HiddenRefreshMs)
                RefreshStatus(verify: correcting);

            if (checkDue)
            {
                _lastCheck = now;
                Enforce();
                // Tam hız açıkken devir ölçümü hedefi yansıtmaz; düzeltme yapma
                if (correcting && _mismatch == 0 && !_state.Boost)
                {
                    Correct(ref _fixedCpuSpeed, _state.CpuRpm, _state.CpuPercent, cpuTemp, AddrCpuSpeeds, _fixedCpuRpm);
                    Correct(ref _fixedGpuSpeed, _state.GpuRpm, _state.GpuPercent, gpuTemp, AddrGpuSpeeds, _fixedGpuRpm);
                }
            }

            if (_failures >= FailureLimit || _busy >= FailureLimit)
                _state = _state with { Message = "EC erişimi yeniden sağlandı." };
            _failures = _busy = 0;
        }
        catch (TimeoutException)
        {
            // Kilit başka bir programda; EC'nin kendisi sağlam, yeniden açmak gerekmez
            if (++_busy == FailureLimit)
                _state = _state with { Message = "EC başka bir program tarafından meşgul tutuluyor; fan durumu güncellenemiyor." };
        }
        catch (Exception ex)
        {
            // Kalıcı arıza (ör. uykudan sonra sürücü tanıtıcısı geçersiz): bildir ve bağlantıyı yeniden kur
            if (++_failures >= FailureLimit)
            {
                Fail("EC'ye erişilemiyor (" + ex.Message + "); bağlantı yeniden kuruluyor…", permanent: false);
                _nextOpenTry = now; // ilk yeniden deneme bir sonraki turda
            }
        }
    }

    /// <param name="verify">true ise yüzde ve devir de doğrulanmış (çift) okunur; false ise salt görüntü için tek okuma.</param>
    private void RefreshStatus(bool verify, string? message = null)
    {
        // Mod, programın istediği moddur; program henüz bir şey uygulamadıysa EC'den okunur
        var select = _intent;
        if (select == FanSelect.Bilinmiyor)
            select = (Read(AddrFanMode) & ModeMask) switch
            {
                ModeAuto => FanSelect.Otomatik,
                ModeAdvanced => FanSelect.Ozel,
                _ => FanSelect.Bilinmiyor
            };

        _state = _state with
        {
            CpuPercent = verify ? Read(AddrCpuPercent) : ReadOnce(AddrCpuPercent),
            GpuPercent = verify ? Read(AddrGpuPercent) : ReadOnce(AddrGpuPercent),
            CpuRpm = ReadRpm(AddrCpuRpm, verify),
            GpuRpm = ReadRpm(AddrGpuRpm, verify),
            Boost = (Read(AddrBoost) & BoostBit) != 0,
            ManualBoost = _manualBoost,
            AutoBoostActive = _autoBoost,
            Selected = select,
            FixedCpuRpm = _intent == FanSelect.Sabit ? _fixedCpuRpm : null,
            FixedGpuRpm = _intent == FanSelect.Sabit ? _fixedGpuRpm : null,
            Perf = ToPerfMode(Read(AddrShift)),
            Message = message ?? _state.Message
        };
        _lastRefresh = Environment.TickCount64;
    }

    private int ReadRpm(byte addr, bool verify)
    {
        // 16 bitlik sayaç iki ayrı okumayla alınır; arada yüksek bayt değiştiyse (ör. ~1870 RPM
        // civarında 0x00FF→0x0100 geçişi) düşük bayt yeniden okunur, yoksa yırtık değer çıkar
        byte lowAddr = (byte)(addr + 1);
        byte hi = verify ? Read(addr) : ReadOnce(addr);
        byte lo = verify ? Read(lowAddr) : ReadOnce(lowAddr);
        _cacheGen[addr] = _cacheGen[lowAddr] = 0;
        byte hi2 = verify ? Read(addr) : ReadOnce(addr);
        if (hi2 != hi)
        {
            hi = hi2;
            lo = verify ? Read(lowAddr) : ReadOnce(lowAddr);
        }
        int raw = (hi << 8) | lo;
        if (raw == 0) return 0;
        int rpm = 478000 / raw;
        return rpm is > 0 and < 12000 ? rpm : 0;
    }

    public void Dispose()
    {
        if (_ec != null)
        {
            bool held = Acquire(CommandLockMs);
            NewBatch();
            // Korumanın açtığı tam hızı program kapanırken geri bırak (kilit alınamadıysa fanlar tam hızda kalır; güvenli taraf)
            try { if (held && _autoBoost && !_manualBoost) SetBoostBit(false); } catch { }
            CloseEc();
        }
        _retryOpen = false;
        ReleaseAll();
        try { _ecMutex?.Dispose(); } catch { }
        _ecMutex = null;
    }
}

/// <summary>Fabrika yedeği için derleme zamanında üretilen JSON dönüştürücü (yansıma kullanmaz).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FanController.Backup))]
internal sealed partial class BackupJsonContext : JsonSerializerContext { }
