using System.Text.Json;

namespace SicaklikPaneli;

public enum PanelMode { Otomatik, HerZaman, Kapali }
public enum Corner { SolUst, SagUst, SolAlt, SagAlt }

/// <summary>Program açılırken fanlara ne yapılacağı. Dokunma: EC'de ne varsa o kalır.</summary>
public enum FanStartup { Dokunma, Otomatik, Ozel, SabitDevir }

public sealed class Settings
{
    public PanelMode Mode { get; set; } = PanelMode.Otomatik;
    public Corner Corner { get; set; } = Corner.SolUst;
    public int Opacity { get; set; } = 80; // yüzde

    // Fan ayarları
    public FanStartup FanStartup { get; set; } = FanStartup.Dokunma;
    public int[]? CpuFanTemps { get; set; }
    public int[]? CpuFanSpeeds { get; set; }
    public int[]? GpuFanTemps { get; set; }
    public int[]? GpuFanSpeeds { get; set; }
    public bool FanProtection { get; set; }          // sıcaklık eşiği aşılınca tam hız
    public int FanProtectionTemp { get; set; } = 90;
    public int FanFixedRpm { get; set; } = 3000;     // sabit devir modunun hedefi

    /// <summary>Program açılırken uygulanacak performans modu (null: dokunma).</summary>
    public PerfMode? PerfStartup { get; set; }

    // Ekran kartı hız aşırtma (MHz)
    public bool GpuOcEnabled { get; set; }
    public int GpuOcCore { get; set; }
    public int GpuOcMemory { get; set; }
    /// <summary>Hız aşırtma açıkken program düzgün kapanmazsa false kalır; sonraki açılışta aşırtma uygulanmaz.</summary>
    public bool GpuOcCleanExit { get; set; } = true;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SicaklikPaneli");
    private static readonly string FilePath = Path.Combine(Dir, "ayarlar.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
