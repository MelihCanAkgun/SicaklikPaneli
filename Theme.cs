using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>Ortak renkler ve çizim yardımcıları.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(17, 17, 20);
    public static readonly Color Card = Color.FromArgb(27, 27, 32);
    public static readonly Color CardBorder = Color.FromArgb(42, 42, 50);
    public static readonly Color Track = Color.FromArgb(48, 48, 58);
    public static readonly Color Text = Color.FromArgb(238, 238, 242);
    public static readonly Color Muted = Color.FromArgb(140, 140, 152);
    public static readonly Color Accent = Color.FromArgb(229, 56, 59);
    public static readonly Color Cpu = Color.FromArgb(90, 170, 255);
    public static readonly Color Gpu = Color.FromArgb(120, 220, 120);
    public static readonly Color Warning = Color.FromArgb(255, 70, 70);
    public static readonly Color Ok = Color.FromArgb(110, 220, 110);

    public static Color TempColor(float? t) => t switch
    {
        null => Color.Gray,
        < 60 => Ok,
        < 75 => Color.FromArgb(240, 220, 90),
        < 88 => Color.FromArgb(255, 150, 50),
        _ => Warning
    };

    public static Font Font(float px, bool bold = false, string family = "Segoe UI") =>
        new(family, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        using var p = RoundedRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    /// <summary>Yatay doluluk çubuğu (0–100).</summary>
    public static void Bar(Graphics g, RectangleF r, float? percent, Color color)
    {
        FillRounded(g, Track, r, r.Height / 2);
        if (percent is not float v) return;
        float w = r.Width * Math.Clamp(v, 0, 100) / 100f;
        if (w < r.Height) w = v > 0.5f ? r.Height : 0;
        if (w > 0) FillRounded(g, color, new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2);
    }

    /// <summary>İçinde ünlem olan uyarı üçgeni.</summary>
    public static void WarningTriangle(Graphics g, RectangleF r, Color? color = null)
    {
        using var p = new GraphicsPath();
        p.AddPolygon(new[]
        {
            new PointF(r.X + r.Width / 2, r.Y),
            new PointF(r.Right, r.Bottom),
            new PointF(r.X, r.Bottom)
        });
        using var b = new SolidBrush(color ?? Warning);
        using var pen = new Pen(color ?? Warning, r.Width * 0.12f) { LineJoin = LineJoin.Round };
        g.FillPath(b, p);
        g.DrawPath(pen, p);

        using var dark = new SolidBrush(Color.FromArgb(20, 20, 24));
        float cx = r.X + r.Width / 2, w = r.Width * 0.13f;
        g.FillRectangle(dark, cx - w / 2, r.Y + r.Height * 0.36f, w, r.Height * 0.32f);
        g.FillRectangle(dark, cx - w / 2, r.Y + r.Height * 0.76f, w, w);
    }
}
