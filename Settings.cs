using System.Text.Json;

namespace SicaklikPaneli;

public enum PanelMode { Otomatik, HerZaman, Kapali }
public enum Corner { SolUst, SagUst, SolAlt, SagAlt }

public sealed class Settings
{
    public PanelMode Mode { get; set; } = PanelMode.Otomatik;
    public Corner Corner { get; set; } = Corner.SolUst;
    public int Opacity { get; set; } = 80; // yüzde

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
