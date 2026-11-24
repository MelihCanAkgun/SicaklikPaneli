using System.Runtime.InteropServices;
using LibreHardwareMonitor.PawnIo;

namespace SicaklikPaneli;

/// <summary>
/// İşlemci ve ekran kartının ısıl kısıtlamaya (thermal throttling) girip girmediğini okur.
/// CPU: Intel durum kayıtları (MSR). GPU: NVIDIA NVML "clocks throttle reasons".
/// </summary>
/// <remarks>
/// CPU tarafında her ölçümde yalnızca iki MSR okunur (paket + o an çalışılan çekirdek);
/// iş parçacığı yakınlığı (affinity) değiştirilmez. Paket sıcaklığı çekirdeklerin en
/// yükseğini izlediğinden paket durum biti, tüm çekirdekleri tek tek okumadan kısıtlamayı yakalar.
/// "Log" (yapışkan) bitleri kullanılmaz: temizlemek MSR'ye yazmayı gerektirir.
/// </remarks>
public sealed class ThrottleMonitor : IDisposable
{
    private const uint IA32_THERM_STATUS = 0x19C;
    private const uint IA32_TEMPERATURE_TARGET = 0x1A2;
    private const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;
    private const ulong ThermalStatusBit = 1 << 0;  // sıcaklık sınırına ulaşıldı, hız kesiliyor
    private const ulong ProchotBit = 1 << 2;        // PROCHOT# sinyali etkin
    private const ulong CpuMask = ThermalStatusBit | ProchotBit;

    // nvmlClocksThrottleReason*
    private const ulong NvHwSlowdown = 0x8, NvSwThermal = 0x20, NvHwThermal = 0x40;
    private const ulong GpuMask = NvHwSlowdown | NvSwThermal | NvHwThermal;

    private readonly object _lock = new();
    private IntelMsr? _msr;
    private readonly float _tjMax = 100;
    private IntPtr _nvDevice;
    private bool _nvReady;
    private bool _disposed;

    // Kısa süreli kısıtlamalar da fark edilsin diye işareti birkaç saniye tut.
    // Environment.TickCount64: tekdüze (saat değişiminden etkilenmez) ve DateTime'dan ucuz.
    private const long HoldMs = 3000;
    private long _cpuUntil, _gpuUntil;

    public ThrottleMonitor(string cpuName)
    {
        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _msr = new IntelMsr();
                if (_msr.ReadMsr(IA32_TEMPERATURE_TARGET, out ulong target))
                {
                    float tj = (target >> 16) & 0xFF;
                    if (tj is >= 70 and <= 125) _tjMax = tj;
                }
            }
            catch
            {
                // Yarım açılmış sürücü tanıtıcısı sızmasın
                try { _msr?.Close(); } catch { }
                _msr = null;
            }
        }

        bool nvInit = false;
        try
        {
            nvInit = nvmlInit_v2() == NVML_SUCCESS;
            if (nvInit && nvmlDeviceGetHandleByIndex_v2(0, out _nvDevice) == NVML_SUCCESS)
                _nvReady = true;
        }
        catch { _nvReady = false; } // nvml.dll yok / giriş noktası yok: GPU denetimi kapalı kalır

        // Başlatıldı ama aygıt alınamadıysa NVML başvuru sayacını geri bırak
        if (nvInit && !_nvReady)
        {
            try { nvmlShutdown(); } catch { }
        }
    }

    public bool CpuThrottling(float? cpuTemp)
    {
        bool now = false;
        lock (_lock)
        {
            if (_msr != null)
            {
                try
                {
                    if (_msr.ReadMsr(IA32_PACKAGE_THERM_STATUS, out ulong pkg))
                        now |= (pkg & CpuMask) != 0;
                    if (!now && _msr.ReadMsr(IA32_THERM_STATUS, out ulong core))
                        now |= (core & CpuMask) != 0;
                }
                catch { }
            }
        }

        // Yedek ölçüt: sıcaklık üst sınıra (TjMax) dayandıysa işlemci hız kesiyordur
        if (cpuTemp is float t && t >= _tjMax - 1) now = true;

        long tick = Environment.TickCount64;
        if (now) _cpuUntil = tick + HoldMs;
        return tick < _cpuUntil;
    }

    public bool GpuThrottling() => GpuThrottling(gpuAwake: true);

    /// <summary>
    /// GPU kısıtlama durumunu döndürür. <paramref name="gpuAwake"/> false ise (dGPU uykuda,
    /// sensörler değer vermiyor) NVML sorgulanmaz; böylece uyuyan kart gereksiz yere uyandırılmaz.
    /// </summary>
    public bool GpuThrottling(bool gpuAwake)
    {
        if (gpuAwake)
        {
            lock (_lock)
            {
                if (_nvReady)
                {
                    try
                    {
                        if (nvmlDeviceGetCurrentClocksThrottleReasons(_nvDevice, out ulong reasons) == NVML_SUCCESS
                            && (reasons & GpuMask) != 0)
                            _gpuUntil = Environment.TickCount64 + HoldMs;
                    }
                    catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
                    {
                        // Kalıcı hata: her saniye istisna fırlatıp CPU harcamamak için NVML'i bırak
                        _nvReady = false;
                        try { nvmlShutdown(); } catch { }
                    }
                    catch { }
                }
            }
        }
        return Environment.TickCount64 < _gpuUntil;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try { _msr?.Close(); } catch { }
            _msr = null;

            if (_nvReady)
            {
                _nvReady = false;
                try { nvmlShutdown(); } catch { }
            }
            _nvDevice = IntPtr.Zero;
        }
    }

    private const int NVML_SUCCESS = 0;

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
