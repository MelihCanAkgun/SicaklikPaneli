using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SicaklikPaneli;

/// <summary>Oyun üstünde duran, tıklamaları geçiren küçük panel.</summary>
public sealed class OverlayForm : Form
{
    public const int LogicalWidth = 206, LogicalHeight = 58;
    private Snapshot _data = Snapshot.Empty;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(14, 14, 17);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        float scale = DeviceDpi / 96f;
        Size = new Size((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        using var path = Theme.RoundedRect(new RectangleF(0, 0, Width, Height), 9 * scale);
        Region = new Region(path);
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
        if (Visible) Invalidate();
    }

    public void PlaceOn(Screen screen, Corner corner)
    {
        int m = LogicalToDeviceUnits(10);
        var b = screen.Bounds;
        int x = corner is Corner.SolUst or Corner.SolAlt ? b.Left + m : b.Right - Width - m;
        int y = corner is Corner.SolUst or Corner.SagUst ? b.Top + m : b.Bottom - Height - m;
        Location = new Point(x, y);
    }

    /// <summary>Oyunlar z-sırasını bozabildiği için paneli tekrar en üste iter.</summary>
    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, Width / (float)LogicalWidth, _data);

    public static void Render(Graphics g, float scale, Snapshot data)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.FromArgb(14, 14, 17));
        g.ScaleTransform(scale, scale);

        DrawRow(g, 7, "CPU", Theme.Cpu, data.CpuTemp, data.CpuLoad, data.CpuThrottle);
        DrawRow(g, 31, "GPU", Theme.Gpu, data.GpuTemp, data.GpuLoad, data.GpuThrottle);
    }

    private static void DrawRow(Graphics g, float y, string label, Color color, float? temp, float? load, bool throttle)
    {
        const float h = 20;
        Theme.FillRounded(g, color, new RectangleF(9, y + 3, 3, h - 6), 1.5f);

        using var labelFont = Theme.Font(12, true);
        using var valueFont = Theme.Font(15, true);
        using var loadFont = Theme.Font(13);
        using var labelBrush = new SolidBrush(color);
        using var tempBrush = new SolidBrush(throttle ? Theme.Warning : Theme.TempColor(temp));
        using var loadBrush = new SolidBrush(Color.FromArgb(214, 214, 220));
        var mid = new StringFormat { LineAlignment = StringAlignment.Center };
        var right = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };

        g.DrawString(label, labelFont, labelBrush, new RectangleF(17, y, 36, h), mid);
        g.DrawString(temp.HasValue ? $"{temp.Value:0}°C" : "--°C", valueFont, tempBrush, new RectangleF(50, y, 62, h), right);
        g.DrawString(load.HasValue ? $"{load.Value:0}%" : "--%", loadFont, loadBrush, new RectangleF(112, y, 54, h), right);

        if (throttle) Theme.WarningTriangle(g, new RectangleF(176, y + 3, 16, 14));
    }
}
