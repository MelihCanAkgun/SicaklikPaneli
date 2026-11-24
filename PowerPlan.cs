using System.Runtime.InteropServices;

namespace SicaklikPaneli;

/// <summary>Performans moduna uygun Windows güç planını etkinleştirir.</summary>
public static partial class PowerPlan
{
    // Windows'un yerleşik güç planları
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    // powercfg süreci başlatmak yerine aynı işi yapan Windows işlevi doğrudan çağrılır
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    public static void Apply(PerfMode mode)
    {
        Guid guid;
        switch (mode)
        {
            case PerfMode.Turbo: guid = HighPerformance; break;
            case PerfMode.Dengeli: guid = Balanced; break;
            default: return;
        }
        // Plan sistemde yoksa (ör. kaldırılmışsa) Windows hata döndürür; powercfg'de olduğu gibi sessizce geçilir
        try { PowerSetActiveScheme(IntPtr.Zero, in guid); }
        catch { }
    }
}
