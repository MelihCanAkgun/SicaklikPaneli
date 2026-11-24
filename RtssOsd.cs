using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32;

namespace SicaklikPaneli;

/// <summary>
/// RivaTuner Statistics Server (RTSS) paylaşımlı belleğine OSD yazısı yazar.
/// RTSS bu yazıyı oyunun içine çizer; böylece özel (exclusive) tam ekranda da görünür.
/// Yapı düzeni RTSSSharedMemory.h (v2.x) ile aynıdır.
/// </summary>
public static class RtssOsd
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353; // 'RTSS'

    // Girdi sahibi adı (bilerek "SicaklikPaneli"); karşılaştırma için hazır ANSI baytları
    private static ReadOnlySpan<byte> OwnerBytes => "SicaklikPaneli"u8;

    // RTSS_SHARED_MEMORY alan konumları
    private const int OffVersion = 4, OffOsdEntrySize = 20, OffOsdArrOffset = 24, OffOsdArrSize = 28,
                      OffOsdFrame = 32, OffBusy = 36;
    // RTSS_SHARED_MEMORY_OSD_ENTRY alan konumları
    private const int OffSzOsd = 0, OffSzOwner = 256, OffSzOsdEx = 512, SzOsdLen = 256, SzOwnerLen = 256, SzOsdExLen = 4096;

    private static readonly Encoding Ansi = Encoding.Latin1; // '°' = 0xB0, her karakter tek bayt

    // Görünüm önbelleği: her saniye OpenFileMapping/MapViewOfFile yapmamak için açık tutulur.
    // RTSS çökerse imza 'RTSS' olarak kalabilir; bu yüzden belirli aralıklarla kapatılıp yeniden açılır.
    private const int ReopenEvery = 10;
    private static readonly object Sync = new();
    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static unsafe byte* _base;
    private static long _capacity;
    private static int _uses;
    private static bool _shutdown;
    // RTSS çalışmıyorken her saniye açmayı deneyip istisna üretmemek için bekleme süresi
    private const long RetryOpenMs = 3000;
    private static long _nextOpenTry;

    /// <summary>Yazıyı RTSS'e gönderir. RTSS çalışmıyorsa false döner.</summary>
    public static bool Update(string text)
    {
        lock (Sync)
        {
            if (_shutdown) return false;
            return Access(text);
        }
    }

    /// <summary>Bizim OSD girdimizi RTSS'ten kaldırır.</summary>
    public static void Release()
    {
        lock (Sync)
        {
            Access(null);
            CloseView(); // gösterge kapalıyken RTSS belleğini tutmaya gerek yok
        }
    }

    /// <summary>
    /// Program kapanırken çağrılır: girdimizi kaldırır ve sonradan gelen Update çağrılarını
    /// yok sayar (arka plan döngüsü kapanış sırasında girdiyi yeniden almasın).
    /// </summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            _shutdown = true;
            Access(null);
            CloseView();
        }
    }

    private static unsafe bool OpenView()
    {
        if (_view != null)
        {
            if (++_uses < ReopenEvery) return true;
            CloseView();
        }
        else if (Environment.TickCount64 < _nextOpenTry)
        {
            return false; // az önce açılamadı (RTSS çalışmıyor); birkaç saniye sonra yeniden denenir
        }

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _mmf = mmf;
            _view = view;
            _base = p + view.PointerOffset;
            _capacity = view.Capacity;
            _uses = 0;
            return true;
        }
        catch
        {
            view?.Dispose();
            mmf?.Dispose();
            _nextOpenTry = Environment.TickCount64 + RetryOpenMs;
            return false; // RTSS çalışmıyor
        }
    }

    private static unsafe void CloseView()
    {
        if (_view != null)
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _view.Dispose();
            _view = null;
        }
        _mmf?.Dispose();
        _mmf = null;
        _base = null;
        _capacity = 0;
    }

    // Sync kilidi altında çağrılmalıdır.
    private static unsafe bool Access(string? text)
    {
        if (!OpenView()) return false;
        try
        {
            byte* p = _base;
            if (_capacity < OffBusy + 4 || *(uint*)p != Signature)
            {
                // RTSS kapandı (0xDEAD) ya da bellek hazır değil; bölümü başka bir istemci açık tutuyorsa
                // her saniye aç/kapat yapılmasın diye birkaç saniye sonra yeniden açılacak
                CloseView();
                _nextOpenTry = Environment.TickCount64 + RetryOpenMs;
                return false;
            }
            uint version = *(uint*)(p + OffVersion);
            if (version < 0x00020000)
            {
                CloseView();
                _nextOpenTry = Environment.TickCount64 + RetryOpenMs;
                return false;
            }

            bool useLock = version >= 0x0002000E;
            ref int busy = ref *(int*)(p + OffBusy);
            if (useLock)
            {
                // _interlockedbittestandset(&dwBusy, 0)
                int spins = 0;
                long deadline = 0;
                while ((Interlocked.Or(ref busy, 1) & 1) != 0)
                {
                    if (++spins > 200)
                    {
                        // Yazma: RTSS meşgul, bir sonraki turda tekrar dene. Silme (kapanış/gösterge kapatma)
                        // sonradan yeniden denenmez; girdi oyunda kalmasın diye en çok ~50 ms daha beklenir.
                        if (text != null) return true;
                        long now = Environment.TickCount64;
                        if (deadline == 0) deadline = now + 50;
                        else if (now >= deadline) return true;
                        Thread.Sleep((spins & 7) == 0 ? 1 : 0);
                        continue;
                    }
                    Thread.Yield();
                }
            }

            try
            {
                uint entrySize = *(uint*)(p + OffOsdEntrySize);
                uint arrOffset = *(uint*)(p + OffOsdArrOffset);
                uint arrSize = *(uint*)(p + OffOsdArrSize);
                bool hasEx = version >= 0x00020007;

                // Bozuk/beklenmedik düzen: görünüm dışına yazmak süreci çökertir, dokunma
                uint minEntry = hasEx ? (uint)(OffSzOsdEx + SzOsdExLen) : (uint)(OffSzOwner + SzOwnerLen);
                if (entrySize < minEntry || (ulong)arrOffset + (ulong)arrSize * entrySize > (ulong)_capacity)
                    return false;

                // Metni bir kez, yığında ANSI'ye çevir (Latin1: karakter başına tek bayt)
                int max = hasEx ? SzOsdExLen : SzOsdLen;
                Span<byte> buf = stackalloc byte[max - 1];
                int n = 0;
                if (text != null) n = Ansi.GetBytes(text.AsSpan(0, Math.Min(text.Length, max - 1)), buf);
                ReadOnlySpan<byte> bytes = buf[..n];

                // 1. tur: kendi girdimizi ara. 2. tur: boş bir girdi kap (yalnızca yazarken).
                for (int pass = 0; pass < 2; pass++)
                {
                    for (uint i = 1; i < arrSize; i++) // 0. girdi RTSS'in kendisine ayrılmış
                    {
                        byte* e = p + arrOffset + (ulong)i * entrySize;
                        byte* owner = e + OffSzOwner;

                        if (pass == 1)
                        {
                            if (text == null) return true;
                            if (owner[0] == 0) WriteBytes(owner, OwnerBytes, SzOwnerLen);
                        }

                        if (!FieldEquals(owner, SzOwnerLen, OwnerBytes)) continue;

                        if (text == null)
                        {
                            new Span<byte>(e + OffSzOsd, SzOsdLen).Clear();
                            new Span<byte>(owner, SzOwnerLen).Clear();
                            if (hasEx) new Span<byte>(e + OffSzOsdEx, SzOsdExLen).Clear();
                        }
                        else if (hasEx)
                        {
                            // Metin değişmediyse yeniden yazma, çerçeve sayacını artırma
                            if (pass == 0 && e[OffSzOsd] == 0 && FieldEquals(e + OffSzOsdEx, SzOsdExLen, bytes)) return true;
                            WriteBytes(e + OffSzOsdEx, bytes, SzOsdExLen);
                            new Span<byte>(e + OffSzOsd, SzOsdLen).Clear();
                        }
                        else
                        {
                            if (pass == 0 && FieldEquals(e + OffSzOsd, SzOsdLen, bytes)) return true;
                            WriteBytes(e + OffSzOsd, bytes, SzOsdLen);
                        }

                        Interlocked.Increment(ref *(int*)(p + OffOsdFrame));
                        return true;
                    }
                }
                return true; // boş girdi yok ama RTSS çalışıyor
            }
            finally
            {
                if (useLock) Interlocked.And(ref busy, ~1);
            }
        }
        catch
        {
            CloseView();
            return false; // RTSS çalışmıyor
        }
    }

    /// <summary>Hedefi temizler ve baytları null sonlandırmalı olarak yazar.</summary>
    private static unsafe void WriteBytes(byte* dst, ReadOnlySpan<byte> src, int max)
    {
        var span = new Span<byte>(dst, max);
        span.Clear();
        src[..Math.Min(src.Length, max - 1)].CopyTo(span);
    }

    /// <summary>Null sonlandırmalı alan, verilen baytlarla birebir aynı mı?</summary>
    private static unsafe bool FieldEquals(byte* field, int max, ReadOnlySpan<byte> value)
    {
        if (value.Length >= max) return false;
        var span = new ReadOnlySpan<byte>(field, value.Length + 1);
        return span[value.Length] == 0 && span[..value.Length].SequenceEqual(value);
    }

    // ---------------- RTSS'i bulma / başlatma ----------------

    public static string? FindExe()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Unwinder\RTSS", @"SOFTWARE\Unwinder\RTSS" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("InstallDir") is string dir)
            {
                var exe = Path.Combine(dir, "RTSS.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        foreach (var dir in new[] { @"C:\Program Files (x86)\RivaTuner Statistics Server", @"C:\Program Files\RivaTuner Statistics Server" })
        {
            var exe = Path.Combine(dir, "RTSS.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static bool IsRunning()
    {
        var procs = Process.GetProcessesByName("RTSS");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>RTSS'in paylaşımlı belleği şu an geçerli mi (süreç taramasından çok daha ucuz)?</summary>
    private static unsafe bool IsMemoryLive()
    {
        lock (Sync)
            return _view != null && _capacity >= 4 && *(uint*)_base == Signature;
    }

    /// <summary>RTSS kuruluysa ve çalışmıyorsa simge durumunda başlatır.</summary>
    public static void EnsureStarted()
    {
        if (IsMemoryLive()) return; // bellek canlıysa RTSS zaten çalışıyor
        var exe = FindExe();
        if (exe == null) return;
        if (IsRunning()) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                WindowStyle = ProcessWindowStyle.Minimized
            });
        }
        catch { }
    }

    /// <summary>OSD metnini RTSS etiketleriyle hazırlar.</summary>
    public static string Format(Snapshot s)
    {
        static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
        static string Row(string label, Color lc, float? t, float? l, bool throttle)
        {
            string temp = t.HasValue ? $"{t.Value:0}°C" : "--°C";
            string load = l.HasValue ? $"{l.Value:0}%" : "--%";
            Color tc = throttle ? Theme.Warning : Theme.TempColor(t);
            string warn = throttle ? $"  <C={Hex(Theme.Warning)}>[!] KISITLAMA<C>" : "";
            return $"<C={Hex(lc)}>{label}<C>  <C={Hex(tc)}>{temp}<C>  {load}{warn}";
        }
        return Row("CPU", Theme.Cpu, s.CpuTemp, s.CpuLoad, s.CpuThrottle) + "\n" +
               Row("GPU", Theme.Gpu, s.GpuTemp, s.GpuLoad, s.GpuThrottle);
    }
}
