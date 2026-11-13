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
    private const string Owner = "SicaklikPaneli";
    private const uint Signature = 0x52545353; // 'RTSS'

    // RTSS_SHARED_MEMORY alan konumları
    private const int OffVersion = 4, OffOsdEntrySize = 20, OffOsdArrOffset = 24, OffOsdArrSize = 28,
                      OffOsdFrame = 32, OffBusy = 36;
    // RTSS_SHARED_MEMORY_OSD_ENTRY alan konumları
    private const int OffSzOsd = 0, OffSzOwner = 256, OffSzOsdEx = 512, SzOsdExLen = 4096;

    private static readonly Encoding Ansi = Encoding.Latin1; // '°' = 0xB0

    /// <summary>Yazıyı RTSS'e gönderir. RTSS çalışmıyorsa false döner.</summary>
    public static bool Update(string text) => Access(text);

    /// <summary>Bizim OSD girdimizi RTSS'ten kaldırır.</summary>
    public static void Release() => Access(null);

    private static unsafe bool Access(string? text)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try
            {
                p += view.PointerOffset;
                if (*(uint*)p != Signature) return false;
                uint version = *(uint*)(p + OffVersion);
                if (version < 0x00020000) return false;

                bool useLock = version >= 0x0002000E;
                ref int busy = ref *(int*)(p + OffBusy);
                if (useLock)
                {
                    // _interlockedbittestandset(&dwBusy, 0)
                    int spins = 0;
                    while ((Interlocked.Or(ref busy, 1) & 1) != 0)
                    {
                        if (++spins > 200) return true; // RTSS meşgul, bir sonraki turda tekrar dene
                        Thread.Yield();
                    }
                }

                try
                {
                    uint entrySize = *(uint*)(p + OffOsdEntrySize);
                    uint arrOffset = *(uint*)(p + OffOsdArrOffset);
                    uint arrSize = *(uint*)(p + OffOsdArrSize);
                    bool hasEx = version >= 0x00020007;

                    // 1. tur: kendi girdimizi ara. 2. tur: boş bir girdi kap (yalnızca yazarken).
                    for (int pass = 0; pass < 2; pass++)
                    {
                        for (uint i = 1; i < arrSize; i++) // 0. girdi RTSS'in kendisine ayrılmış
                        {
                            byte* e = p + arrOffset + i * entrySize;
                            byte* owner = e + OffSzOwner;

                            if (pass == 1)
                            {
                                if (text == null) return true;
                                if (owner[0] == 0) WriteStr(owner, Owner, 256);
                            }

                            if (ReadStr(owner, 256) != Owner) continue;

                            if (text == null)
                            {
                                new Span<byte>(e, 256).Clear();
                                new Span<byte>(owner, 256).Clear();
                                if (hasEx) new Span<byte>(e + OffSzOsdEx, SzOsdExLen).Clear();
                            }
                            else if (hasEx)
                            {
                                WriteStr(e + OffSzOsdEx, text, SzOsdExLen);
                                new Span<byte>(e + OffSzOsd, 256).Clear();
                            }
                            else
                            {
                                WriteStr(e + OffSzOsd, text, 256);
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
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch
        {
            return false; // RTSS çalışmıyor
        }
    }

    private static unsafe void WriteStr(byte* dst, string s, int max)
    {
        var bytes = Ansi.GetBytes(s);
        int n = Math.Min(bytes.Length, max - 1);
        var span = new Span<byte>(dst, max);
        span.Clear();
        bytes.AsSpan(0, n).CopyTo(span);
    }

    private static unsafe string ReadStr(byte* src, int max)
    {
        var span = new ReadOnlySpan<byte>(src, max);
        int len = span.IndexOf((byte)0);
        return Ansi.GetString(len < 0 ? span : span[..len]);
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

    public static bool IsRunning() => Process.GetProcessesByName("RTSS").Length > 0;

    /// <summary>RTSS kuruluysa ve çalışmıyorsa simge durumunda başlatır.</summary>
    public static void EnsureStarted()
    {
        if (IsRunning()) return;
        var exe = FindExe();
        if (exe == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
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
