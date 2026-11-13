using System.Reflection;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware.Motherboard.Lpc.EC;

namespace SicaklikPaneli;

/// <summary>
/// MSI dizüstülerin gömülü denetleyicisi (EC) üzerinden fanları okur ve yönetir.
/// Yalnızca adres düzeni doğrulanmış bellenim ailelerinde yazma yapar.
/// Tüm çağrılar tek bir iş parçacığından (sensör döngüsü) yapılmalıdır.
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

    private WindowsEmbeddedControllerIO? _ec;
    private Func<byte, byte>? _read;
    private Action<byte, byte>? _write;

    private FanState _state = FanState.Unavailable("Fan denetimi başlatılıyor…");
    private bool _manualBoost, _autoBoost;
    private DateTime _lastHot = DateTime.MinValue;
    private int _revision;

    public FanState State => _state;

    public FanController()
    {
        try
        {
            _ec = new WindowsEmbeddedControllerIO();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(WindowsEmbeddedControllerIO);
            _read = type.GetMethod("ReadByte", flags)!.CreateDelegate<Func<byte, byte>>(_ec);
            _write = type.GetMethod("WriteByte", flags)!.CreateDelegate<Action<byte, byte>>(_ec);

            string fw = ReadFirmware();
            if (!SupportedFirmware.Any(p => fw.StartsWith(p, StringComparison.Ordinal)))
            {
                Fail($"Bu cihazın fan denetimi desteklenmiyor (bellenim: {(fw.Length > 0 ? fw : "okunamadı")}).");
                return;
            }

            var cpu = ReadCurve(AddrCpuTemps, AddrCpuSpeeds);
            var gpu = ReadCurve(AddrGpuTemps, AddrGpuSpeeds);
            if (!LooksSane(cpu) || !LooksSane(gpu))
            {
                Fail("EC'deki fan tablosu beklenen biçimde değil; güvenlik için fan denetimi kapalı.");
                return;
            }

            var (factoryCpu, factoryGpu) = LoadOrCreateBackup(cpu, gpu);
            _state = new FanState
            {
                Available = true, Firmware = fw, Cpu = cpu, Gpu = gpu,
                FactoryCpu = factoryCpu, FactoryGpu = factoryGpu, Revision = ++_revision
            };
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Fail("EC'ye erişilemedi: " + ex.Message);
        }
    }

    private void Fail(string message)
    {
        _state = FanState.Unavailable(message);
        try { _ec?.Dispose(); } catch { }
        _ec = null;
    }

    // ---------------- Düşük seviye: doğrulamalı okuma / yazma ----------------

    /// <summary>Başka yazılımlar da EC'yi yokladığı için arka arkaya iki aynı okuma beklenir.</summary>
    private byte Read(byte addr)
    {
        byte prev = _read!(addr);
        for (int i = 0; i < 4; i++)
        {
            byte cur = _read(addr);
            if (cur == prev) return cur;
            prev = cur;
        }
        return prev;
    }

    /// <summary>Yazar ve geri okuyarak doğrular.</summary>
    private bool Write(byte addr, byte value)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Read(addr) == value) return true;
            _write!(addr, value);
        }
        return Read(addr) == value;
    }

    private string ReadFirmware()
    {
        var sb = new StringBuilder();
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

    /// <summary>Okunan tablo gerçekten bir fan eğrisine benziyor mu? (Yanlış adres düzenine karşı önlem)</summary>
    private static bool LooksSane(FanCurve c)
    {
        for (int i = 0; i < 6; i++)
        {
            if (c.Temps[i] is < 30 or > 105) return false;
            if (i > 0 && c.Temps[i] <= c.Temps[i - 1]) return false;
        }
        return c.Speeds.All(s => s is >= 0 and <= FanCurve.MaxSpeed);
    }

    private bool WriteCurve(byte tempAddr, byte speedAddr, FanCurve c)
    {
        bool ok = true;
        for (int i = 0; i < 6; i++) ok &= Write((byte)(tempAddr + i), (byte)c.Temps[i]);
        for (int i = 0; i < 7; i++) ok &= Write((byte)(speedAddr + i), (byte)c.Speeds[i]);
        return ok;
    }

    // ---------------- Fabrika yedeği ----------------

    private sealed record Backup(int[] CpuTemps, int[] CpuSpeeds, int[] GpuTemps, int[] GpuSpeeds, string Firmware, DateTime Date);

    /// <summary>İlk çalıştırmada EC'deki özgün eğriyi dosyaya kaydeder; sonra hep o dosya "fabrika ayarı"dır.</summary>
    private (FanCurve cpu, FanCurve gpu) LoadOrCreateBackup(FanCurve cpu, FanCurve gpu)
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                var b = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath));
                if (b != null)
                {
                    var bc = new FanCurve(b.CpuTemps, b.CpuSpeeds);
                    var bg = new FanCurve(b.GpuTemps, b.GpuSpeeds);
                    if (bc.Temps.Length == 6 && bc.Speeds.Length == 7 && LooksSane(bc) &&
                        bg.Temps.Length == 6 && bg.Speeds.Length == 7 && LooksSane(bg))
                        return (bc, bg);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            var backup = new Backup(cpu.Temps, cpu.Speeds, gpu.Temps, gpu.Speeds, ReadFirmware(), DateTime.Now);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return (cpu.Copy(), gpu.Copy());
    }

    // ---------------- Komutlar ----------------

    /// <summary>Özel eğriyi EC'ye yazar ve "gelişmiş" fan moduna geçer.</summary>
    public bool ApplyCustom(FanCurve cpu, FanCurve gpu)
    {
        if (_ec == null) return false;
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
            bool ok = WriteCurve(AddrCpuTemps, AddrCpuSpeeds, cpu) & WriteCurve(AddrGpuTemps, AddrGpuSpeeds, gpu);
            ok &= SetMode(ModeAdvanced);
            _manualBoost = false;
            ok &= SetBoostBit(_autoBoost);
            ReloadCurves(ok ? "Eğri uygulandı." : "Bazı değerler yazılamadı; EC'deki güncel eğri gösteriliyor.");
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
            _manualBoost = false;
            bool ok = SetMode(ModeAuto) & SetBoostBit(_autoBoost);
            RefreshStatus(ok ? "Otomatik moda geçildi." : "Mod değiştirilemedi.");
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
            _manualBoost = on;
            bool ok = SetBoostBit(on || _autoBoost);
            RefreshStatus(ok ? (on ? "Tam hız açıldı." : "Tam hız kapatıldı.") : "Tam hız ayarı değiştirilemedi.");
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
            if (value == null || ToPerfMode(Read(AddrShift)) == PerfMode.Bilinmiyor)
            {
                _state = _state with { Message = "Performans modu bu cihazda değiştirilemiyor." };
                return false;
            }
            bool ok = Write(AddrShift, value.Value);
            RefreshStatus(ok ? $"Performans modu: {mode}." : "Performans modu değiştirilemedi.");
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

    private bool SetMode(byte mode) => Write(AddrFanMode, (byte)((Read(AddrFanMode) & ~ModeMask) | mode));

    private bool SetBoostBit(bool on)
    {
        byte cur = Read(AddrBoost);
        return Write(AddrBoost, (byte)(on ? cur | BoostBit : cur & ~BoostBit));
    }

    private void ReloadCurves(string message)
    {
        _state = _state with
        {
            Cpu = ReadCurve(AddrCpuTemps, AddrCpuSpeeds),
            Gpu = ReadCurve(AddrGpuTemps, AddrGpuSpeeds),
            Revision = ++_revision
        };
        RefreshStatus(message);
    }

    // ---------------- Her turda: sıcaklık koruması + durum ----------------

    /// <summary>
    /// Sıcaklık korumasını işletir ve fan durumunu günceller.
    /// Koruma açıksa, sıcaklık eşiği aşıldığında fanlar tam hıza alınır; sıcaklık düşünce geri bırakılır.
    /// </summary>
    public void Tick(float? cpuTemp, float? gpuTemp, bool protectionEnabled, int protectionTemp)
    {
        if (_ec == null) return;
        try
        {
            float hottest = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);
            bool want = _autoBoost;
            if (!protectionEnabled) want = false;
            else if (hottest >= protectionTemp) { want = true; _lastHot = DateTime.UtcNow; }
            else if (_autoBoost && hottest <= protectionTemp - 8 && DateTime.UtcNow - _lastHot > TimeSpan.FromSeconds(15)) want = false;

            if (want != _autoBoost)
            {
                _autoBoost = want;
                SetBoostBit(_manualBoost || _autoBoost);
            }
            RefreshStatus();
        }
        catch { }
    }

    private void RefreshStatus(string? message = null)
    {
        byte mode = (byte)(Read(AddrFanMode) & ModeMask);
        _state = _state with
        {
            CpuPercent = Read(AddrCpuPercent),
            GpuPercent = Read(AddrGpuPercent),
            CpuRpm = ReadRpm(AddrCpuRpm),
            GpuRpm = ReadRpm(AddrGpuRpm),
            Boost = (Read(AddrBoost) & BoostBit) != 0,
            AutoBoostActive = _autoBoost,
            Perf = ToPerfMode(Read(AddrShift)),
            Mode = mode switch
            {
                ModeAuto => FanEcMode.Otomatik,
                ModeBasic => FanEcMode.Temel,
                ModeAdvanced => FanEcMode.Ozel,
                _ => FanEcMode.Bilinmiyor
            },
            Message = message ?? _state.Message
        };
    }

    private int ReadRpm(byte addr)
    {
        int raw = (Read(addr) << 8) | Read((byte)(addr + 1));
        if (raw == 0) return 0;
        int rpm = 478000 / raw;
        return rpm is > 0 and < 12000 ? rpm : 0;
    }

    public void Dispose()
    {
        // Korumanın açtığı tam hızı program kapanırken geri bırak
        try { if (_ec != null && _autoBoost && !_manualBoost) SetBoostBit(false); } catch { }
        try { _ec?.Dispose(); } catch { }
        _ec = null;
    }
}
