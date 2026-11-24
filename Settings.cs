using System.Text.Json;
using System.Text.Json.Serialization;

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
    public int FanFixedRpm { get; set; } = 3000;     // eski tek hedef; ayrı hedefler yoksa ikisine de bu kullanılır
    public int? FanFixedCpuRpm { get; set; }         // sabit devir: işlemci fanı hedefi
    public int? FanFixedGpuRpm { get; set; }         // sabit devir: ekran kartı fanı hedefi

    /// <summary>Program açılırken uygulanacak performans modu (null: dokunma).</summary>
    public PerfMode? PerfStartup { get; set; }

    // Ekran kartı hız aşırtma (MHz)
    public bool GpuOcEnabled { get; set; }
    public int GpuOcCore { get; set; }
    public int GpuOcMemory { get; set; }
    /// <summary>Hız aşırtma açıkken program düzgün kapanmazsa false kalır; sonraki açılışta aşırtma uygulanmaz.</summary>
    public bool GpuOcCleanExit { get; set; } = true;

    // Arayüzün kabul ettiği sınırlar (dosyadan gelen değerler bunlara sıkıştırılır)
    private const int MinOpacity = 20, MaxOpacity = 99;               // TrayApp'teki panel saydamlığı sınırı
    private const int MinProtectionTemp = 70, MaxProtectionTemp = 97; // FanView'daki koruma eşiği sınırı

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SicaklikPaneli");
    private static readonly string FilePath = Path.Combine(Dir, "ayarlar.json");
    private static readonly string TempPath = FilePath + ".tmp";     // atomik kaydetme için ara dosya
    private static readonly string CorruptPath = FilePath + ".bak";  // okunamayan dosyanın yedeği

    /// <summary>Arayüz ve arka plan iş parçacıklarından aynı anda gelen kaydetmeleri sıraya koyar.</summary>
    private static readonly object SaveLock = new();

    /// <summary>Diske en son yazılan içerik; aynı içerik tekrar yazılmaz.</summary>
    private byte[]? _lastSaved;

    public static Settings Load()
    {
        byte[] data;
        try
        {
            if (!File.Exists(FilePath)) return new Settings();
            data = File.ReadAllBytes(FilePath);
        }
        catch { return new Settings(); } // dosya okunamadı (kilitli vb.): bozuk sayma, dokunma

        Settings? s = null;
        try
        {
            // Elle düzenlenip BOM ile kaydedilmiş dosya da okunabilsin (bayt okuyucu BOM'u atlamaz)
            ReadOnlySpan<byte> json = data;
            if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
            if (!json.IsEmpty)
                s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings);
        }
        catch { }

        if (s == null)
        {
            // Boş ya da bozuk dosya (ör. yazılırken elektrik kesildi): incelemek için yedeğini al, varsayılanlarla devam et
            try { File.Copy(FilePath, CorruptPath, overwrite: true); } catch { }
            return new Settings();
        }

        s.Normalize();
        return s;
    }

    /// <summary>Dosyadan gelen geçersiz değerleri güvenli aralığa çeker; program kötü bir durumla açılmasın.</summary>
    private void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = PanelMode.Otomatik;
        if (!Enum.IsDefined(Corner)) Corner = Corner.SolUst;
        if (!Enum.IsDefined(FanStartup)) FanStartup = FanStartup.Dokunma;
        if (PerfStartup is PerfMode p && !Enum.IsDefined(p)) PerfStartup = null;

        Opacity = Math.Clamp(Opacity, MinOpacity, MaxOpacity);
        FanProtectionTemp = Math.Clamp(FanProtectionTemp, MinProtectionTemp, MaxProtectionTemp);

        FanFixedRpm = ClampRpm(FanFixedRpm);
        if (FanFixedCpuRpm is int c) FanFixedCpuRpm = ClampRpm(c);
        if (FanFixedGpuRpm is int g) FanFixedGpuRpm = ClampRpm(g);

        GpuOcCore = Math.Clamp(GpuOcCore, 0, PerfView.CoreLimit);
        GpuOcMemory = Math.Clamp(GpuOcMemory, 0, PerfView.MemoryLimit);

        // Eğri boyutu yanlışsa (6 eşik, 7 hız) eğri düzenleyici ve EC yazımı dizi dışına taşar: eğriyi yok say
        if (CpuFanTemps is not { Length: 6 } || CpuFanSpeeds is not { Length: 7 } ||
            GpuFanTemps is not { Length: 6 } || GpuFanSpeeds is not { Length: 7 })
        {
            CpuFanTemps = CpuFanSpeeds = GpuFanTemps = GpuFanSpeeds = null;
        }

        static int ClampRpm(int v) => Math.Clamp(v, FanController.FixedMinRpm, FanController.FixedMaxRpm);
    }

    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, SettingsJsonContext.Default.Settings);
                if (_lastSaved != null && json.AsSpan().SequenceEqual(_lastSaved)) return; // değişiklik yok

                Directory.CreateDirectory(Dir);
                // Önce ara dosyaya yazıp diske indir, sonra tek adımda asıl dosyanın yerine koy:
                // yazma sırasında elektrik kesilirse eski dosya sağlam kalır.
                using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(json);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(TempPath, FilePath, overwrite: true);
                _lastSaved = json;
            }
            catch { }
        }
    }
}

/// <summary>Derleme zamanında üretilen JSON dönüştürücüsü: açılışta yansıma maliyeti olmaz.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext { }
