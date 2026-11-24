namespace SicaklikPaneli;

/// <summary>Bir andaki tüm ölçümler.</summary>
public record Snapshot(float? CpuTemp, float? CpuLoad, float? GpuTemp, float? GpuLoad, string CpuName, string GpuName)
{
    public float? CpuPower { get; init; }      // W
    public float? CpuClock { get; init; }      // MHz (en hızlı çekirdek)
    public float? GpuPower { get; init; }      // W
    public float? GpuMemUsed { get; init; }    // MB
    public float? GpuMemTotal { get; init; }   // MB
    public float? RamUsed { get; init; }       // GB
    public float? RamTotal { get; init; }      // GB
    public bool CpuThrottle { get; init; }
    public bool GpuThrottle { get; init; }
    // Varsayılan paylaşılır: her Snapshot'ta yeni bir FanState ayrılmasın (FanState değişmez)
    public FanState Fan { get; init; } = FanState.Waiting;

    public static readonly Snapshot Empty = new(null, null, null, null, "CPU", "GPU");
}

/// <summary>
/// Fan eğrisi: 6 sıcaklık eşiği ve 7 hız. Speeds[0] ilk eşiğin altında,
/// Speeds[k+1] ise Temps[k] aşıldığında uygulanır. Hızlar MSI ölçeğindedir (0–150).
/// </summary>
public sealed record FanCurve(int[] Temps, int[] Speeds)
{
    public const int MinTemp = 35, MaxTemp = 97, MaxSpeed = 150, MinTopSpeed = 100, MinGap = 2;

    public FanCurve Copy() => new((int[])Temps.Clone(), (int[])Speeds.Clone());

    public bool SameAs(FanCurve? o) => o != null && Temps.SequenceEqual(o.Temps) && Speeds.SequenceEqual(o.Speeds);

    /// <summary>Verilen sıcaklıkta eğrinin istediği hız.</summary>
    public int SpeedAt(float temp)
    {
        int level = 0;
        for (int i = 0; i < Temps.Length; i++) if (temp >= Temps[i]) level = i + 1;
        return Speeds[level];
    }

    /// <summary>Güvenlik kuralları: eşikler artan, hızlar azalmayan, en üst kademe en az %100.</summary>
    public bool IsValid(out string error)
    {
        error = "";
        if (Temps.Length != 6 || Speeds.Length != 7) { error = "Eğri boyutu hatalı."; return false; }
        for (int i = 0; i < 6; i++)
        {
            if (Temps[i] < MinTemp || Temps[i] > MaxTemp) { error = $"Sıcaklık eşikleri {MinTemp}–{MaxTemp}°C aralığında olmalı."; return false; }
            if (i > 0 && Temps[i] < Temps[i - 1] + MinGap) { error = "Sıcaklık eşikleri artan sırada olmalı."; return false; }
        }
        for (int i = 0; i < 7; i++)
        {
            if (Speeds[i] < 0 || Speeds[i] > MaxSpeed) { error = $"Fan hızı 0–{MaxSpeed} aralığında olmalı."; return false; }
            if (i > 0 && Speeds[i] < Speeds[i - 1]) { error = "Fan hızı sıcaklık arttıkça düşemez."; return false; }
        }
        if (Speeds[6] < MinTopSpeed) { error = $"En yüksek kademe en az %{MinTopSpeed} olmalı."; return false; }
        return true;
    }
}

/// <summary>Fan modu: programın seçtiği mod (henüz bir şey seçilmediyse EC'den okunan).</summary>
public enum FanSelect { Bilinmiyor, Otomatik, Ozel, Sabit }

/// <summary>MSI "Shift" performans modu (Dragon Center'daki performans seviyesi).</summary>
public enum PerfMode { Turbo, Sport, Dengeli, Eco, Bilinmiyor }

/// <summary>Fanların anlık durumu ve EC'de kayıtlı eğriler.</summary>
public sealed record FanState
{
    public bool Available { get; init; }
    public string Message { get; init; } = "";
    public string Firmware { get; init; } = "";
    public int CpuPercent { get; init; }
    public int GpuPercent { get; init; }
    public int CpuRpm { get; init; }
    public int GpuRpm { get; init; }
    public FanSelect Selected { get; init; } = FanSelect.Bilinmiyor;
    public PerfMode Perf { get; init; } = PerfMode.Bilinmiyor;
    public bool Boost { get; init; }            // Cooler Boost şu an açık
    public bool AutoBoostActive { get; init; }  // sıcaklık koruması tarafından açıldı
    public bool ManualBoost { get; init; }      // tam hız kullanıcı tarafından açıldı
    public int? FixedCpuRpm { get; init; }      // sabit devir modundaysa işlemci fanı hedefi
    public int? FixedGpuRpm { get; init; }      // sabit devir modundaysa ekran kartı fanı hedefi
    public int CommandSeq { get; init; }        // EC'ye işlenmiş son arayüz komutunun sıra numarası
    public FanCurve? Cpu { get; init; }
    public FanCurve? Gpu { get; init; }
    public FanCurve? FactoryCpu { get; init; }
    public FanCurve? FactoryGpu { get; init; }
    public int Revision { get; init; }          // eğriler EC'den her yeniden okunduğunda artar

    public static FanState Unavailable(string message) => new() { Available = false, Message = message };

    /// <summary>Henüz fan bilgisi gelmediğinde kullanılan ortak örnek.</summary>
    public static readonly FanState Waiting = Unavailable("Fan bilgisi bekleniyor…");
}
