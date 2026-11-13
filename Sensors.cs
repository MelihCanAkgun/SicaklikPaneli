using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SicaklikPaneli;

/// <summary>LibreHardwareMonitor üzerinden CPU/GPU sıcaklık ve kullanımını okur.</summary>
public sealed class Sensors : IDisposable
{
    private readonly Computer _pc;
    private readonly UpdateVisitor _visitor = new();
    private ThrottleMonitor? _throttle;

    public Sensors()
    {
        _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        _pc.Open();
    }

    public Snapshot Read()
    {
        _pc.Accept(_visitor);

        IHardware? cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // Harici ekran kartını tercih et (NVIDIA > AMD > Intel)
        IHardware? gpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                      ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
                      ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);

        float? cpuTemp = Pick(cpu, SensorType.Temperature, "CPU Package", "Core Max", "Core Average", "Tctl/Tdie", "Tdie");
        float? cpuLoad = Pick(cpu, SensorType.Load, "CPU Total");
        float? gpuTemp = Pick(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot");
        float? gpuLoad = Pick(gpu, SensorType.Load, "GPU Core", "D3D 3D");

        string cpuName = cpu?.Name ?? "CPU";
        _throttle ??= new ThrottleMonitor(cpuName);
        var (ramUsed, ramTotal) = ReadRam();

        return new Snapshot(cpuTemp, cpuLoad, gpuTemp, gpuLoad, cpuName, gpu?.Name ?? "GPU")
        {
            CpuPower = Exact(cpu, SensorType.Power, "CPU Package", "Package"),
            CpuClock = MaxClock(cpu),
            GpuPower = Exact(gpu, SensorType.Power, "GPU Package", "GPU Power"),
            GpuMemUsed = Exact(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            GpuMemTotal = Exact(gpu, SensorType.SmallData, "GPU Memory Total"),
            RamUsed = ramUsed,
            RamTotal = ramTotal,
            CpuThrottle = _throttle.CpuThrottling(cpuTemp),
            GpuThrottle = _throttle.GpuThrottling()
        };
    }

    private static IEnumerable<ISensor> All(IHardware hw, SensorType type) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors))
          .Where(s => s.SensorType == type && s.Value.HasValue);

    /// <summary>Tercih edilen isimlerden ilk bulunanı döndürür; yoksa uygun bir yedek seçer.</summary>
    private static float? Pick(IHardware? hw, SensorType type, params string[] preferred)
    {
        if (hw == null) return null;
        var sensors = All(hw, type).ToList();
        foreach (var name in preferred)
        {
            var s = sensors.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        if (sensors.Count == 0) return null;
        return type == SensorType.Temperature ? sensors.Max(s => s.Value) : sensors[0].Value;
    }

    /// <summary>Yalnızca verilen isimlerden biri varsa değer döndürür (yedek yok).</summary>
    private static float? Exact(IHardware? hw, SensorType type, params string[] names)
    {
        if (hw == null) return null;
        var sensors = All(hw, type).ToList();
        foreach (var name in names)
        {
            var s = sensors.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return null;
    }

    private static float? MaxClock(IHardware? hw)
    {
        if (hw == null) return null;
        var cores = All(hw, SensorType.Clock)
            .Where(s => !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)).ToList();
        return cores.Count == 0 ? null : cores.Max(s => s.Value);
    }

    private static (float? used, float? total) ReadRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (null, null);
        const float gb = 1024f * 1024f * 1024f;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    public void Dispose()
    {
        _throttle?.Dispose();
        _pc.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
