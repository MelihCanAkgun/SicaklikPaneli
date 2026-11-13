using System.Diagnostics;

namespace SicaklikPaneli;

/// <summary>Performans moduna uygun Windows güç planını etkinleştirir.</summary>
public static class PowerPlan
{
    // Windows'un yerleşik güç planları
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public static void Apply(PerfMode mode)
    {
        string? guid = mode switch
        {
            PerfMode.Turbo => HighPerformance,
            PerfMode.Dengeli => Balanced,
            _ => null
        };
        if (guid == null) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", $"/setactive {guid}")
            {
                CreateNoWindow = true, UseShellExecute = false
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
