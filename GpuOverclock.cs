using System.Runtime.InteropServices;

namespace SicaklikPaneli;

/// <summary>Bir saat alanının (çekirdek ya da bellek) hız aşırtma durumu. Değerler MHz.</summary>
public readonly record struct OcClock(bool Editable, int OffsetMhz, int MinMhz, int MaxMhz);

public sealed record OcState(bool Available, string Message, OcClock Core, OcClock Memory)
{
    /// <summary>Ekran kartı uykuda (Optimus): bilgi şu an okunamıyor, kart uyanınca okunabilir.</summary>
    public bool Asleep { get; init; }

    public static OcState Unavailable(string message) => new(false, message, default, default);
}

/// <summary>
/// NVIDIA ekran kartlarında saat hızı ofsetini (hız aşırtma) sürücü üzerinden okur ve ayarlar.
/// MSI Afterburner ve Dragon Center'ın kullandığı NVAPI "Pstates20" arayüzünü kullanır.
/// Voltaja dokunmaz; ofset kalıcı değildir, yeniden başlatmada sıfırlanır.
/// </summary>
public static unsafe class GpuOverclock
{
    private const uint IdInitialize = 0x0150E828, IdEnumPhysicalGpus = 0xE5AC921F,
                       IdGetPstates20 = 0x6FF81213, IdSetPstates20 = 0x0F4DAE6B;

    // NV_GPU_PERF_PSTATES20_INFO yapı düzeni (V2/V3 aynı boyutta)
    private const int SizeV2 = 0x1CF8, HeaderSize = 20, PstateSize = 456, ClockSize = 44;
    private const int OffNumPstates = 8, OffNumClocks = 12, OffClocks = 8;
    private const int ClkDomain = 0, ClkType = 4, ClkEditable = 8, ClkDelta = 12, ClkMin = 16, ClkMax = 20;
    private const uint DomainGraphics = 0, DomainMemory = 4;
    private const uint VersionV2 = 0x20000 | SizeV2, VersionV3 = 0x30000 | SizeV2;
    private const int MaxPhysicalGpus = 64;

    // NVAPI dönüş kodları
    private const int NvApiNotInitialized = -4, NvInvalidHandle = -8, NvHandleInvalidated = -10, NvGpuNotPowered = -220;

    // Sık dönen durumlar her okumada yeniden oluşturulmasın
    private static readonly OcState Sleeping = OcState.Unavailable("Ekran kartı şu an uykuda. Bir oyun açıkken bu sayfaya tekrar bak.") with { Asleep = true };
    private static readonly OcState NotSupported = OcState.Unavailable("Ekran kartı hız aşırtmayı desteklemiyor.");

    private static readonly object Sync = new();

    // nvapi64.dll ve işlev işaretçileri bir kez çözülür; GPU tutamacı geçersizleşirse yeniden alınır
    private static bool _resolved, _permanentFailure;
    private static delegate* unmanaged[Cdecl]<int> _initialize;
    private static delegate* unmanaged[Cdecl]<IntPtr*, uint*, int> _enumGpus;
    private static delegate* unmanaged[Cdecl]<IntPtr, void*, int> _get, _set;
    private static IntPtr _gpu;
    private static uint _version;
    private static string _error = "";

    // Yapı için süreç boyunca tek bir yerel tampon (yalnızca kilit altında kullanılır)
    private static byte* _buf;

    /// <summary>Kütüphaneyi ve işlevleri çözer (bir kez); ardından gerekiyorsa NVAPI'yi başlatıp GPU tutamacını alır.</summary>
    private static bool EnsureInit()
    {
        if (_gpu != IntPtr.Zero) return true;
        if (_permanentFailure) return false;

        if (!_resolved)
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(GpuOverclock).Assembly, null, out IntPtr lib))
                return Fail("NVIDIA sürücüsü bulunamadı.");
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr qiPtr))
                return Fail("NVIDIA sürücüsü desteklenmiyor.");

            var qi = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;
            IntPtr init = qi(IdInitialize), enumGpus = qi(IdEnumPhysicalGpus),
                   get = qi(IdGetPstates20), set = qi(IdSetPstates20);
            if (init == IntPtr.Zero || enumGpus == IntPtr.Zero || get == IntPtr.Zero || set == IntPtr.Zero)
                return Fail("NVAPI işlevi bulunamadı.");

            _initialize = (delegate* unmanaged[Cdecl]<int>)init;
            _enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, uint*, int>)enumGpus;
            _get = (delegate* unmanaged[Cdecl]<IntPtr, void*, int>)get;
            _set = (delegate* unmanaged[Cdecl]<IntPtr, void*, int>)set;
            _buf = (byte*)NativeMemory.Alloc(SizeV2);
            _resolved = true;
        }

        // Başlatma ya da sayım geçici olarak başarısız olabilir (ör. sürücü yeni yükleniyor); sonraki çağrıda yeniden denenir
        int rc = _initialize();
        if (rc != 0) { _error = $"NVAPI başlatılamadı ({rc})."; return false; }

        IntPtr* handles = stackalloc IntPtr[MaxPhysicalGpus];
        uint count = 0;
        rc = _enumGpus(handles, &count);
        if (rc != 0 || count == 0) { _error = "NVIDIA ekran kartı bulunamadı."; return false; }

        _gpu = handles[0];
        return true;

        static bool Fail(string message)
        {
            _permanentFailure = true;
            _error = message;
            return false;
        }
    }

    /// <summary>Tutamaç geçersizleşmişse (ör. sürücü yeniden yüklendi) yeniden almak gerekir.</summary>
    private static bool IsStaleHandle(int rc) => rc is NvInvalidHandle or NvHandleInvalidated or NvApiNotInitialized;

    /// <summary>Geçerli ofsetleri ve sürücünün izin verdiği aralıkları okur.</summary>
    public static OcState Read()
    {
        lock (Sync)
        {
            try { return ReadLocked(); }
            catch (Exception ex) { return OcState.Unavailable(ex.Message); }
        }
    }

    private static OcState ReadLocked()
    {
        if (!EnsureInit()) return OcState.Unavailable(_error);

        int rc = GetInfo();
        if (IsStaleHandle(rc))
        {
            _gpu = IntPtr.Zero;
            if (!EnsureInit()) return OcState.Unavailable(_error);
            rc = GetInfo();
        }
        // Optimus dizüstülerde ekran kartı kullanılmadığında uykuya geçer
        if (rc == NvGpuNotPowered) return Sleeping;
        if (rc != 0) return OcState.Unavailable($"Hız aşırtma bilgisi okunamadı ({rc}).");

        byte* buf = _buf;
        int numPstates = *(int*)(buf + OffNumPstates);
        int numClocks = Math.Min(*(int*)(buf + OffNumClocks), 8);
        if (numPstates < 1) return NotSupported;

        OcClock core = default, memory = default;
        for (int j = 0; j < numClocks; j++)
        {
            byte* c = buf + HeaderSize + OffClocks + j * ClockSize; // en yüksek performans durumu (P0)
            uint domain = *(uint*)(c + ClkDomain);
            var clock = new OcClock(
                (*(int*)(c + ClkEditable) & 1) != 0,
                *(int*)(c + ClkDelta) / 1000,
                *(int*)(c + ClkMin) / 1000,
                *(int*)(c + ClkMax) / 1000);
            if (domain == DomainGraphics) core = clock;
            else if (domain == DomainMemory) memory = clock;
        }
        if (!core.Editable && !memory.Editable)
            return new OcState(false, "Bu ekran kartında hız aşırtma kilitli.", core, memory);
        return new OcState(true, "", core, memory);
    }

    /// <summary>Yapıyı tampona okur. Sürüm henüz bilinmiyorsa V2, olmazsa V3 denenir.</summary>
    private static int GetInfo()
    {
        if (_version != 0) return GetInfo(_version);
        int rc = GetInfo(VersionV2);
        if (rc == 0) { _version = VersionV2; return 0; }
        // Kart uykuda: sürüm denemesi anlamsız; V3'ün (ör. yapı sürümü) hata kodu uyku bilgisini gizlemesin
        if (rc == NvGpuNotPowered) return rc;
        int rc3 = GetInfo(VersionV3);
        if (rc3 == 0) { _version = VersionV3; return 0; }
        // İkisi de başarısız: uyku ve bayat tutamaç kodları (yeniden deneme bunlara göre yapılır) önceliklidir
        return rc3 == NvGpuNotPowered || IsStaleHandle(rc3) ? rc3 : rc;
    }

    private static int GetInfo(uint version)
    {
        new Span<byte>(_buf, SizeV2).Clear();
        *(uint*)_buf = version;
        return _get(_gpu, _buf);
    }

    /// <summary>Çekirdek ve bellek saat ofsetlerini (MHz) ayarlar. Sürücünün aralığı dışına çıkmaz.</summary>
    public static bool Apply(int coreMhz, int memoryMhz, out string error) =>
        Apply(coreMhz, memoryMhz, out error, out _);

    /// <summary>
    /// <see cref="Apply(int, int, out string)"/> ile aynı; ayrıca ekran kartı uykuda olduğu için
    /// uygulanamadıysa <paramref name="asleep"/> true olur (kart uyandırılmaz, hiçbir şey yazılmaz).
    /// </summary>
    public static bool Apply(int coreMhz, int memoryMhz, out string error, out bool asleep)
    {
        lock (Sync)
        {
            error = "";
            asleep = false;
            OcState state;
            try { state = ReadLocked(); }
            catch (Exception ex) { error = ex.Message; return false; }
            if (!state.Available) { error = state.Message; asleep = state.Asleep; return false; }

            bool ok = true;
            if (state.Core.Editable)
                ok &= SetClock(DomainGraphics, ClampOffset(coreMhz, state.Core), ref error);
            if (state.Memory.Editable)
                ok &= SetClock(DomainMemory, ClampOffset(memoryMhz, state.Memory), ref error);
            return ok;
        }
    }

    /// <summary>Ofseti sürücü aralığına sığdırır; sıfıra (sıfırlama) her zaman izin verilir.
    /// Sürücü tutarsız bir aralık bildirse bile istisna atmaz.</summary>
    private static int ClampOffset(int mhz, OcClock clock) =>
        Math.Clamp(mhz, Math.Min(0, clock.MinMhz), Math.Max(0, clock.MaxMhz));

    private static bool SetClock(uint domain, int offsetMhz, ref string error)
    {
        try
        {
            byte* buf = _buf;
            new Span<byte>(buf, SizeV2).Clear();
            *(uint*)buf = _version;
            *(int*)(buf + OffNumPstates) = 1;
            *(int*)(buf + OffNumClocks) = 1;
            byte* c = buf + HeaderSize + OffClocks;
            *(uint*)(c + ClkDomain) = domain;
            *(int*)(c + ClkDelta) = offsetMhz * 1000;
            int rc = _set(_gpu, buf);
            if (rc != 0) { error = $"Sürücü ofseti kabul etmedi ({rc})."; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
