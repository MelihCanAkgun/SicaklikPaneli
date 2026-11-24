using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SicaklikPaneli;

/// <summary>Oyun üstünde duran, tıklamaları geçiren küçük panel.</summary>
public sealed class OverlayForm : Form
{
    public const int LogicalWidth = 206, LogicalHeight = 58;
    private static readonly Color Back = Color.FromArgb(14, 14, 17);
    private static readonly Color LoadColor = Color.FromArgb(214, 214, 220);
    private static readonly StringFormat AlignMid = new() { LineAlignment = StringAlignment.Center };
    private static readonly StringFormat AlignRight = new() { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };

    private Snapshot _data = Snapshot.Empty;
    private ShownState _shown = ShownState.Of(Snapshot.Empty);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        ApplyScale(DeviceDpi);
    }

    /// <summary>Boyutu ve yuvarlak köşeli bölgeyi verilen DPI'ya göre ayarlar.</summary>
    private void ApplyScale(int dpi)
    {
        float scale = dpi / 96f;
        Size = new Size((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        using var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 9 * scale);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>
    /// Panel farklı DPI'lı bir ekrana taşındığında Windows önerilen boyutu uygular, ama yuvarlak bölge eski
    /// boyutta kalırdı (kenarlar kırpılır / taşar). Boyutu ve bölgeyi yeni DPI'ya göre yeniden kur.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale(e.DeviceDpiNew);
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void SetData(Snapshot s)
    {
        _data = s;
        // Yalnızca ekranda görünen bir şey (yuvarlanmış değer, renk, uyarı) değiştiyse yeniden çiz
        var shown = ShownState.Of(s);
        if (shown == _shown) return;
        _shown = shown;
        if (Visible) Invalidate();
    }

    public void PlaceOn(Screen screen, Corner corner)
    {
        // Konum değişimi DPI değişimini (dolayısıyla boyut değişimini) tetikleyebilir; o durumda yeni boyutla bir kez daha hesapla
        for (int i = 0; i < 2; i++)
        {
            var size = Size;
            int m = LogicalToDeviceUnits(10);
            var b = screen.Bounds;
            int x = corner is Corner.SolUst or Corner.SolAlt ? b.Left + m : b.Right - Width - m;
            int y = corner is Corner.SolUst or Corner.SagUst ? b.Top + m : b.Bottom - Height - m;
            Location = new Point(x, y);
            if (Size == size) break;
        }
    }

    /// <summary>
    /// Oyunlar z-sırasını bozabildiği için paneli tekrar en üste iter. Her 500 ms'de çağrılır; panel zaten
    /// en üstteyse (üstünde görünür pencere yoksa) SetWindowPos ve onun ürettiği pencere iletileri atlanır.
    /// </summary>
    public void BringToTop()
    {
        if (Visible && IsTopVisible()) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private const uint GW_HWNDPREV = 3;
    private const int WS_EX_TOPMOST = 0x8, GWL_EXSTYLE = -20;
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);

    /// <summary>Panel en üst katmandaysa ve z-sırasında üstünde görünür bir pencere yoksa true.</summary>
    private bool IsTopVisible()
    {
        IntPtr h = Handle;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) return false;
        // Görünmez pencereler çizimi örtmez; ilk görünür pencerede dur (yürüyüş en fazla birkaç yüz adım)
        for (int i = 0; i < 1024; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);
            if (h == IntPtr.Zero) return true;
            if (IsWindowVisible(h)) return false;
        }
        return false;
    }

    // Render arka planı zaten tamamen boyadığı için ayrıca BackColor ile doldurmaya gerek yok
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, Width / (float)LogicalWidth, _data);

    public static void Render(Graphics g, float scale, Snapshot data)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Back);
        g.ScaleTransform(scale, scale);

        DrawRow(g, 7, "CPU", Theme.Cpu, data.CpuTemp, data.CpuLoad, data.CpuThrottle);
        DrawRow(g, 31, "GPU", Theme.Gpu, data.GpuTemp, data.GpuLoad, data.GpuThrottle);
    }

    private static string TempText(float? temp) => temp.HasValue ? $"{temp.Value:0}°C" : "--°C";
    private static string LoadText(float? load) => load.HasValue ? $"{load.Value:0}%" : "--%";
    private static Color TempBrushColor(float? temp, bool throttle) => throttle ? Theme.Warning : Theme.TempColor(temp);

    private static void DrawRow(Graphics g, float y, string label, Color color, float? temp, float? load, bool throttle)
    {
        const float h = 20;
        Theme.FillRounded(g, color, new RectangleF(9, y + 3, 3, h - 6), 1.5f);

        // Yazı tipleri, fırçalar ve biçimler paylaşılır (her karede yeniden oluşturulmaz)
        g.DrawString(label, Theme.SharedFont(12, true), Theme.SharedBrush(color), new RectangleF(17, y, 36, h), AlignMid);
        g.DrawString(TempText(temp), Theme.SharedNumber(16), Theme.SharedBrush(TempBrushColor(temp, throttle)), new RectangleF(50, y, 62, h), AlignRight);
        g.DrawString(LoadText(load), Theme.SharedFont(13), Theme.SharedBrush(LoadColor), new RectangleF(112, y, 54, h), AlignRight);

        if (throttle) Theme.WarningTriangle(g, new RectangleF(176, y + 3, 16, 14));
    }

    /// <summary>Panelde gerçekte görünen her şey; değişmediyse yeniden çizim gerekmez.</summary>
    private readonly record struct ShownState(string CpuTemp, string CpuLoad, int CpuColor, bool CpuThrottle,
                                         string GpuTemp, string GpuLoad, int GpuColor, bool GpuThrottle)
    {
        public static ShownState Of(Snapshot s) => new(
            TempText(s.CpuTemp), LoadText(s.CpuLoad), TempBrushColor(s.CpuTemp, s.CpuThrottle).ToArgb(), s.CpuThrottle,
            TempText(s.GpuTemp), LoadText(s.GpuLoad), TempBrushColor(s.GpuTemp, s.GpuThrottle).ToArgb(), s.GpuThrottle);
    }
}
