using System.Runtime.InteropServices;

namespace SicaklikPaneli;

internal static partial class Native
{
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x8000000;
    public const int WS_EX_TOPMOST = 0x8;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    // Kaynak üretimli P/Invoke (LibraryImport): çalışma anında marshalling kodu üretilmez
    [LibraryImport("user32.dll")] public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] private static unsafe partial int GetClassName(IntPtr hWnd, char* buf, int max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(IntPtr hIcon);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "TaskManagerWindow"
    };

    // Sınıf adını string üretmeden aramak için (500 ms'de bir çağrılıyor, çöp üretmesin)
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ShellClassLookup =
        ShellClasses.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Ön plandaki pencere tüm ekranı kaplıyorsa (oyun, tam ekran uygulama) true döner.</summary>
    public static unsafe bool IsForegroundFullscreen(out Screen? screen)
    {
        screen = null;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;

        char* buf = stackalloc char[256];
        int len = GetClassName(hwnd, buf, 256);
        if (len < 0) len = 0;
        if (ShellClassLookup.Contains(new ReadOnlySpan<char>(buf, len))) return false;

        if (!GetWindowRect(hwnd, out RECT r)) return false;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        screen = Screen.FromRectangle(rect);
        var b = screen.Bounds;
        return rect.Left <= b.Left && rect.Top <= b.Top && rect.Right >= b.Right && rect.Bottom >= b.Bottom;
    }
}
