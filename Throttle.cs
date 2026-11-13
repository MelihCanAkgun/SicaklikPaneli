using System.Runtime.InteropServices;
using LibreHardwareMonitor.PawnIo;

namespace SicaklikPaneli;

/// <summary>
/// İşlemci ve ekran kartının ısıl kısıtlamaya (thermal throttling) girip girmediğini okur.
/// CPU: Intel durum kayıtları (MSR). GPU: NVIDIA NVML "clocks throttle reasons".
/// </summary>
public sealed class ThrottleMonitor : IDisposable
{
    private const uint IA32_THERM_STATUS = 0x19C;
    private const uint IA32_TEMPERATURE_TARGET = 0x1A2;
    private const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;
    private const ulong ThermalStatusBit = 1 << 0;  // sıcaklık sınırına ulaşıldı, hız kesiliyor
    private const ulong ProchotBit = 1 << 2;        // PROCHOT# sinyali etkin

    // nvmlClocksThrottleReason*
    private const ulong NvHwSlowdown = 0x8, NvSwThermal = 0x20, NvHwThermal = 0x40;

    private IntelMsr? _msr;
    private readonly float _tjMax = 100;
    private IntPtr _nvDevice;
    private bool _nvReady;

    // Kısa süreli kısıtlamalar da fark edilsin diye işareti birkaç saniye tut
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);
    private DateTime _cpuUntil, _gpuUntil;

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
            catch { _msr = null; }
        }

        try
        {
            if (nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out _nvDevice) == 0)
                _nvReady = true;
        }
        catch { _nvReady = false; }
    }

    public bool CpuThrottling(float? cpuTemp)
    {
        bool now = false;
        try
        {
            if (_msr != null)
            {
                if (_msr.ReadMsr(IA32_PACKAGE_THERM_STATUS, out ulong pkg))
                    now |= (pkg & (ThermalStatusBit | ProchotBit)) != 0;
                if (_msr.ReadMsr(IA32_THERM_STATUS, out ulong core))
                    now |= (core & (ThermalStatusBit | ProchotBit)) != 0;
            }
        }
        catch { }

        // Yedek ölçüt: sıcaklık üst sınıra (TjMax) dayandıysa işlemci hız kesiyordur
        if (cpuTemp is float t && t >= _tjMax - 1) now = true;

        if (now) _cpuUntil = DateTime.UtcNow + Hold;
        return DateTime.UtcNow < _cpuUntil;
    }

    public bool GpuThrottling()
    {
        try
        {
            if (_nvReady && nvmlDeviceGetCurrentClocksThrottleReasons(_nvDevice, out ulong reasons) == 0
                && (reasons & (NvHwSlowdown | NvSwThermal | NvHwThermal)) != 0)
                _gpuUntil = DateTime.UtcNow + Hold;
        }
        catch { }
        return DateTime.UtcNow < _gpuUntil;
    }

    public void Dispose()
    {
        try { _msr?.Close(); } catch { }
        try { if (_nvReady) nvmlShutdown(); } catch { }
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
