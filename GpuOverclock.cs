using System.Runtime.InteropServices;

namespace SicaklikPaneli;

/// <summary>Bir saat alanının (çekirdek ya da bellek) hız aşırtma durumu. Değerler MHz.</summary>
public readonly record struct OcClock(bool Editable, int OffsetMhz, int MinMhz, int MaxMhz);

public sealed record OcState(bool Available, string Message, OcClock Core, OcClock Memory)
{
    public static OcState Unavailable(string message) => new(false, message, default, default);
}

/// <summary>
/// NVIDIA ekran kartlarında saat hızı ofsetini (hız aşırtma) sürücü üzerinden okur ve ayarlar.
/// MSI Afterburner ve Dragon Center'ın kullandığı NVAPI "Pstates20" arayüzünü kullanır.
/// Voltaja dokunmaz; ofset kalıcı değildir, yeniden başlatmada sıfırlanır.
/// </summary>
public static class GpuOverclock
{
    private const uint IdInitialize = 0x0150E828, IdEnumPhysicalGpus = 0xE5AC921F,
                       IdGetPstates20 = 0x6FF81213, IdSetPstates20 = 0x0F4DAE6B;

    // NV_GPU_PERF_PSTATES20_INFO yapı düzeni
    private const int SizeV2 = 0x1CF8, HeaderSize = 20, PstateSize = 456, ClockSize = 44;
    private const int OffNumPstates = 8, OffNumClocks = 12, OffClocks = 8;
    private const int ClkDomain = 0, ClkType = 4, ClkEditable = 8, ClkDelta = 12, ClkMin = 16, ClkMax = 20;
    private const uint DomainGraphics = 0, DomainMemory = 4;
    private static readonly uint[] Versions = { 0x20000 | SizeV2, 0x30000 | SizeV2 };

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumGpusFn([Out] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PstatesFn(IntPtr gpu, IntPtr info);

    private static bool _initialized;
    private static IntPtr _gpu;
    private static PstatesFn? _get, _set;
    private static uint _version;
    private static string _error = "";

    private static bool EnsureInit()
    {
        if (_initialized) return _gpu != IntPtr.Zero;
        _initialized = true;
        try
        {
            T Fn<T>(uint id) where T : Delegate
            {
                IntPtr p = QueryInterface(id);
                if (p == IntPtr.Zero) throw new InvalidOperationException("NVAPI işlevi bulunamadı.");
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }

            int rc = Fn<InitializeFn>(IdInitialize)();
            if (rc != 0) { _error = $"NVAPI başlatılamadı ({rc})."; return false; }

            var handles = new IntPtr[64];
            rc = Fn<EnumGpusFn>(IdEnumPhysicalGpus)(handles, out uint count);
            if (rc != 0 || count == 0) { _error = "NVIDIA ekran kartı bulunamadı."; return false; }

            _get = Fn<PstatesFn>(IdGetPstates20);
            _set = Fn<PstatesFn>(IdSetPstates20);
            _gpu = handles[0];
            return true;
        }
        catch (DllNotFoundException) { _error = "NVIDIA sürücüsü bulunamadı."; return false; }
        catch (Exception ex) { _error = ex.Message; return false; }
    }

    /// <summary>Geçerli ofsetleri ve sürücünün izin verdiği aralıkları okur.</summary>
    public static OcState Read()
    {
        if (!EnsureInit()) return OcState.Unavailable(_error);
        IntPtr buf = Marshal.AllocHGlobal(SizeV2);
        try
        {
            int rc = -1;
            foreach (uint version in _version != 0 ? new[] { _version } : Versions)
            {
                Clear(buf);
                Marshal.WriteInt32(buf, 0, (int)version);
                rc = _get!(_gpu, buf);
                if (rc == 0) { _version = version; break; }
            }
            if (rc != 0) return OcState.Unavailable($"Hız aşırtma bilgisi okunamadı ({rc}).");

            int numPstates = Marshal.ReadInt32(buf, OffNumPstates);
            int numClocks = Math.Min(Marshal.ReadInt32(buf, OffNumClocks), 8);
            if (numPstates < 1) return OcState.Unavailable("Ekran kartı hız aşırtmayı desteklemiyor.");

            OcClock core = default, memory = default;
            for (int j = 0; j < numClocks; j++)
            {
                int o = HeaderSize + OffClocks + j * ClockSize; // en yüksek performans durumu (P0)
                uint domain = (uint)Marshal.ReadInt32(buf, o + ClkDomain);
                var clock = new OcClock(
                    (Marshal.ReadInt32(buf, o + ClkEditable) & 1) != 0,
                    Marshal.ReadInt32(buf, o + ClkDelta) / 1000,
                    Marshal.ReadInt32(buf, o + ClkMin) / 1000,
                    Marshal.ReadInt32(buf, o + ClkMax) / 1000);
                if (domain == DomainGraphics) core = clock;
                else if (domain == DomainMemory) memory = clock;
            }
            if (!core.Editable && !memory.Editable)
                return new OcState(false, "Bu ekran kartında hız aşırtma kilitli.", core, memory);
            return new OcState(true, "", core, memory);
        }
        catch (Exception ex) { return OcState.Unavailable(ex.Message); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Çekirdek ve bellek saat ofsetlerini (MHz) ayarlar. Sürücünün aralığı dışına çıkmaz.</summary>
    public static bool Apply(int coreMhz, int memoryMhz, out string error)
    {
        error = "";
        var state = Read();
        if (!state.Available) { error = state.Message; return false; }

        bool ok = true;
        if (state.Core.Editable)
            ok &= SetClock(DomainGraphics, Math.Clamp(coreMhz, Math.Min(0, state.Core.MinMhz), state.Core.MaxMhz), ref error);
        if (state.Memory.Editable)
            ok &= SetClock(DomainMemory, Math.Clamp(memoryMhz, Math.Min(0, state.Memory.MinMhz), state.Memory.MaxMhz), ref error);
        return ok;
    }

    private static bool SetClock(uint domain, int offsetMhz, ref string error)
    {
        IntPtr buf = Marshal.AllocHGlobal(SizeV2);
        try
        {
            Clear(buf);
            Marshal.WriteInt32(buf, 0, (int)_version);
            Marshal.WriteInt32(buf, OffNumPstates, 1);
            Marshal.WriteInt32(buf, OffNumClocks, 1);
            int o = HeaderSize + OffClocks;
            Marshal.WriteInt32(buf, o + ClkDomain, (int)domain);
            Marshal.WriteInt32(buf, o + ClkDelta, offsetMhz * 1000);
            int rc = _set!(_gpu, buf);
            if (rc != 0) { error = $"Sürücü ofseti kabul etmedi ({rc})."; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static unsafe void Clear(IntPtr buf) => new Span<byte>((void*)buf, SizeV2).Clear();
}
