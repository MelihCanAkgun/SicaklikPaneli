using System.Runtime.InteropServices;
using System.Text;

namespace SicaklikPaneli;

internal static class Native
{
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x8000000;
    public const int WS_EX_TOPMOST = 0x8;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "TaskManagerWindow"
    };

    /// <summary>Ön plandaki pencere tüm ekranı kaplıyorsa (oyun, tam ekran uygulama) true döner.</summary>
    public static bool IsForegroundFullscreen(out Screen? screen)
    {
        screen = null;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        if (ShellClasses.Contains(sb.ToString())) return false;

        if (!GetWindowRect(hwnd, out RECT r)) return false;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        screen = Screen.FromRectangle(rect);
        var b = screen.Bounds;
        return rect.Left <= b.Left && rect.Top <= b.Top && rect.Right >= b.Right && rect.Bottom >= b.Bottom;
    }
}
