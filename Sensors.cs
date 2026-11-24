using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SicaklikPaneli;

/// <summary>LibreHardwareMonitor üzerinden CPU/GPU sıcaklık ve kullanımını okur.</summary>
/// <remarks>
/// Yalnızca <see cref="Read"/> içinden, tek bir iş parçacığından kullanılmalıdır.
/// Sensör referansları önbellekte tutulur; donanım ya da sensör listesi değiştiğinde
/// (LibreHardwareMonitor olayları) önbellek bir sonraki okumada yeniden kurulur.
/// </remarks>
public sealed class Sensors : IDisposable
{
    private static readonly string[] CpuTempNames = { "CPU Package", "Core Max", "Core Average", "Tctl/Tdie", "Tdie" };
    private static readonly string[] CpuLoadNames = { "CPU Total" };
    private static readonly string[] GpuTempNames = { "GPU Core", "GPU Hot Spot" };
    private static readonly string[] GpuLoadNames = { "GPU Core", "D3D 3D" };
    private static readonly string[] CpuPowerNames = { "CPU Package", "Package" };
    private static readonly string[] GpuPowerNames = { "GPU Package", "GPU Power" };
    private static readonly string[] GpuMemUsedNames = { "GPU Memory Used", "D3D Dedicated Memory Used" };
    private static readonly string[] GpuMemTotalNames = { "GPU Memory Total" };

    private static readonly uint MemStatusSize = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();

    private readonly Computer _pc;
    private ThrottleMonitor? _throttle;
    private bool _disposed;

    // Önbellek: olay işleyicileri yalnızca bu bayrağı kurar, yeniden kurulum Read içinde yapılır.
    private volatile bool _dirty = true;
    private IHardware? _cpu, _gpu;
    private string _cpuName = "CPU", _gpuName = "GPU";
    private Slot _cpuTemp, _cpuLoad, _gpuTemp, _gpuLoad, _cpuPower, _gpuPower, _gpuMemUsed, _gpuMemTotal;
    private ISensor[] _cpuClocks = Array.Empty<ISensor>();

    public Sensors()
    {
        _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        _pc.HardwareAdded += OnHardwareChanged;
        _pc.HardwareRemoved += OnHardwareChanged;
        // Yarıda kalan açılış kapatılmazsa her yeniden denemede sürücü/muteks tanıtıcıları sızar
        try { _pc.Open(); }
        catch
        {
            try { _pc.Close(); } catch { }
            throw;
        }
    }

    public Snapshot Read()
    {
        if (_dirty) Rebuild();

        // Yalnızca kullanılan donanımı güncelle (ör. NVIDIA varken Intel iGPU'yu yoklama)
        Update(_cpu);
        Update(_gpu);
        // Güncelleme sırasında yeni sensör etkinleşmiş olabilir; aynı okumada görülsün
        if (_dirty) Rebuild();

        float? cpuTemp = _cpuTemp.Pick(maxFallback: true);
        float? cpuLoad = _cpuLoad.Pick(maxFallback: false);
        float? gpuTemp = _gpuTemp.Pick(maxFallback: true);
        float? gpuLoad = _gpuLoad.Pick(maxFallback: false);

        _throttle ??= new ThrottleMonitor(_cpuName);
        var (ramUsed, ramTotal) = ReadRam();

        return new Snapshot(cpuTemp, cpuLoad, gpuTemp, gpuLoad, _cpuName, _gpuName)
        {
            CpuPower = _cpuPower.Exact(),
            CpuClock = MaxValue(_cpuClocks),
            GpuPower = _gpuPower.Exact(),
            GpuMemUsed = _gpuMemUsed.Exact(),
            GpuMemTotal = _gpuMemTotal.Exact(),
            RamUsed = ramUsed,
            RamTotal = ramTotal,
            CpuThrottle = _throttle.CpuThrottling(cpuTemp),
            // Sıcaklık ve yük ikisi de yoksa ekran kartı uykudadır: NVML sorgulanıp kart uyandırılmasın
            GpuThrottle = _throttle.GpuThrottling(gpuAwake: gpuTemp.HasValue || gpuLoad.HasValue)
        };
    }

    /// <summary>Donanımı ve alt donanımlarını günceller; bir donanımın hatası diğerlerini etkilemez.</summary>
    private static void Update(IHardware? hw)
    {
        if (hw == null) return;
        try { hw.Update(); } catch { }
        foreach (var sub in hw.SubHardware)
        {
            try { sub.Update(); } catch { }
        }
    }

    private void OnHardwareChanged(IHardware hardware) => _dirty = true;
    private void OnSensorChanged(ISensor sensor) => _dirty = true;

    /// <summary>Donanım seçimini ve sensör referanslarını yeniden kurar.</summary>
    private void Rebuild()
    {
        _dirty = false;
        Subscribe(_cpu, false);
        Subscribe(_gpu, false);

        IHardware? cpu = null, nvidia = null, amd = null, intel = null;
        foreach (var h in _pc.Hardware)
        {
            switch (h.HardwareType)
            {
                case HardwareType.Cpu: cpu ??= h; break;
                case HardwareType.GpuNvidia: nvidia ??= h; break;
                case HardwareType.GpuAmd: amd ??= h; break;
                case HardwareType.GpuIntel: intel ??= h; break;
            }
        }
        // Harici ekran kartını tercih et (NVIDIA > AMD > Intel)
        _cpu = cpu;
        _gpu = nvidia ?? amd ?? intel;
        _cpuName = _cpu?.Name ?? "CPU";
        _gpuName = _gpu?.Name ?? "GPU";

        Subscribe(_cpu, true);
        Subscribe(_gpu, true);

        ISensor[] cpuSensors = Collect(_cpu), gpuSensors = Collect(_gpu);
        _cpuTemp = Slot.Create(cpuSensors, SensorType.Temperature, CpuTempNames);
        _cpuLoad = Slot.Create(cpuSensors, SensorType.Load, CpuLoadNames);
        _cpuPower = Slot.Create(cpuSensors, SensorType.Power, CpuPowerNames);
        _gpuTemp = Slot.Create(gpuSensors, SensorType.Temperature, GpuTempNames);
        _gpuLoad = Slot.Create(gpuSensors, SensorType.Load, GpuLoadNames);
        _gpuPower = Slot.Create(gpuSensors, SensorType.Power, GpuPowerNames);
        _gpuMemUsed = Slot.Create(gpuSensors, SensorType.SmallData, GpuMemUsedNames);
        _gpuMemTotal = Slot.Create(gpuSensors, SensorType.SmallData, GpuMemTotalNames);
        _cpuClocks = Array.FindAll(cpuSensors, s => s.SensorType == SensorType.Clock
                                                 && !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase));
    }

    private void Subscribe(IHardware? hw, bool on)
    {
        if (hw == null) return;
        foreach (var h in hw.SubHardware.Prepend(hw))
        {
            if (on) { h.SensorAdded += OnSensorChanged; h.SensorRemoved += OnSensorChanged; }
            else { h.SensorAdded -= OnSensorChanged; h.SensorRemoved -= OnSensorChanged; }
        }
    }

    /// <summary>Donanımın ve alt donanımlarının sensörleri (sıra korunarak).</summary>
    private static ISensor[] Collect(IHardware? hw)
    {
        if (hw == null) return Array.Empty<ISensor>();
        var list = new List<ISensor>(hw.Sensors);
        foreach (var sub in hw.SubHardware) list.AddRange(sub.Sensors);
        return list.ToArray();
    }

    /// <summary>Değeri olan sensörler arasında en büyük değer; hiçbiri yoksa null.</summary>
    private static float? MaxValue(ISensor[] sensors)
    {
        float? max = null;
        foreach (var s in sensors)
            if (s.Value is float v && (max == null || v > max)) max = v;
        return max;
    }

    /// <summary>Bir ölçüm için aday sensörler: önce tercih sırasına göre isimle eşleşenler, sonra aynı türden tümü.</summary>
    private readonly struct Slot
    {
        private readonly ISensor[]? _preferred; // isim sırasına göre (aynı isimliler donanım sırasında)
        private readonly ISensor[]? _all;       // aynı türdeki tüm sensörler (yedek için)

        private Slot(ISensor[] preferred, ISensor[] all) { _preferred = preferred; _all = all; }

        public static Slot Create(ISensor[] sensors, SensorType type, string[] names)
        {
            var all = Array.FindAll(sensors, s => s.SensorType == type);
            var preferred = new List<ISensor>();
            foreach (var name in names)
                foreach (var s in all)
                    if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) preferred.Add(s);
            return new Slot(preferred.ToArray(), all);
        }

        /// <summary>Verilen isimlerden değeri olan ilkini döndürür (yedek yok).</summary>
        public float? Exact()
        {
            if (_preferred == null) return null;
            foreach (var s in _preferred)
                if (s.Value is float v) return v;
            return null;
        }

        /// <summary>
        /// Tercih edilen isimlerden ilk bulunanı döndürür; yoksa uygun bir yedek seçer
        /// (sıcaklıkta en yüksek değer, diğerlerinde değeri olan ilk sensör).
        /// </summary>
        public float? Pick(bool maxFallback)
        {
            if (Exact() is float v) return v;
            if (_all == null) return null;
            if (maxFallback) return MaxValue(_all);
            foreach (var s in _all)
                if (s.Value is float f) return f;
            return null;
        }
    }

    private static (float? used, float? total) ReadRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = MemStatusSize };
        if (!GlobalMemoryStatusEx(ref m)) return (null, null);
        const float gb = 1024f * 1024f * 1024f;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _throttle?.Dispose(); } catch { }
        try
        {
            Subscribe(_cpu, false);
            Subscribe(_gpu, false);
            _pc.HardwareAdded -= OnHardwareChanged;
            _pc.HardwareRemoved -= OnHardwareChanged;
        }
        catch { }
        try { _pc.Close(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
