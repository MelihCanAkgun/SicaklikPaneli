using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>
/// Ortak renkler ve çizim yardımcıları.
/// Önbellekli (Shared*) nesneler tüm program boyunca paylaşılır: çağıran onları DISPOSE ETMEMELİ.
/// Çizim yalnızca arayüz iş parçacığında yapıldığı için önbellekler kilitsizdir.
/// </summary>
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

    private static readonly Color WarningDark = Color.FromArgb(20, 20, 24);

    public static Color TempColor(float? t) => t switch
    {
        null => Color.Gray,
        < 60 => Ok,
        < 75 => Color.FromArgb(240, 220, 90),
        < 88 => Color.FromArgb(255, 150, 50),
        _ => Warning
    };

    // ---------------- Yazı tipleri ----------------

    // Aile adları açılışta tek bir yazı tipi taramasıyla belirlenir (eskiden her aile için ayrı tarama yapılıyordu)
    private static readonly string SemiboldFamily;
    private static readonly string NumberFamily;

    static Theme()
    {
        bool semibold, bahnschrift;
        using (var fonts = new System.Drawing.Text.InstalledFontCollection())
        {
            var families = fonts.Families;
            semibold = families.Any(f => f.Name.Equals("Segoe UI Semibold", StringComparison.OrdinalIgnoreCase));
            bahnschrift = families.Any(f => f.Name.Equals("Bahnschrift SemiBold", StringComparison.OrdinalIgnoreCase));
            foreach (var f in families) f.Dispose();
        }
        SemiboldFamily = semibold ? "Segoe UI Semibold" : "Segoe UI";
        NumberFamily = bahnschrift ? "Bahnschrift SemiBold" : SemiboldFamily;
    }

    /// <summary>Metin yazı tipi: Segoe UI; vurgulu metinler kalın yerine yarı kalın (Semibold). Çağıran dispose eder.</summary>
    public static Font Font(float px, bool semibold = false) =>
        new(semibold ? SemiboldFamily : "Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Sayılar için dar ve düzgün rakamlı Bahnschrift (yoksa Segoe UI Semibold). Çağıran dispose eder.</summary>
    public static Font Number(float px) => new(NumberFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Dictionary<(float Px, int Kind), Font> FontCache = new();

    /// <summary>Font ile aynı, ancak önbellekten paylaşılan nesne döner. DISPOSE EDİLMEMELİ.</summary>
    public static Font SharedFont(float px, bool semibold = false) => CachedFont(px, semibold ? 1 : 0);

    /// <summary>Number ile aynı, ancak önbellekten paylaşılan nesne döner. DISPOSE EDİLMEMELİ.</summary>
    public static Font SharedNumber(float px) => CachedFont(px, 2);

    private static Font CachedFont(float px, int kind)
    {
        if (!FontCache.TryGetValue((px, kind), out var font))
        {
            // Boyutlar mantıksal pikseldir; ölçek Graphics dönüşümüyle uygulandığı için ölçek başına kopya gerekmez
            font = kind == 2 ? Number(px) : Font(px, kind == 1);
            FontCache[(px, kind)] = font;
        }
        return font;
    }

    /// <summary>Yazının üst kenarından taban çizgisine uzaklık; farklı yazı tiplerini aynı satırda hizalamak için.</summary>
    public static float Ascent(Font f) =>
        f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    // ---------------- Fırça / kalem önbelleği ----------------

    // Programdaki renk kümesi sabit ve küçüktür; yine de beklenmedik renk akışında belleğin büyümemesi için sınır var.
    private const int MaxCachedBrushes = 256, MaxCachedPens = 64;
    private static readonly Dictionary<int, SolidBrush> BrushCache = new();
    private static readonly Dictionary<(int Argb, float Width), Pen> PenCache = new();

    /// <summary>Renge göre paylaşılan düz fırça. DISPOSE EDİLMEMELİ ve rengi değiştirilmemeli.</summary>
    public static SolidBrush SharedBrush(Color c)
    {
        int key = c.ToArgb();
        if (BrushCache.TryGetValue(key, out var brush)) return brush;
        brush = new SolidBrush(c);
        // Sınır aşılırsa önbelleğe alınmaz (dönen fırçayı çöp toplayıcı temizler); dolu önbellek boşaltılmaz,
        // çünkü daha önce verilen fırçalar o anda hâlâ kullanılıyor olabilir.
        if (BrushCache.Count < MaxCachedBrushes) BrushCache[key] = brush;
        return brush;
    }

    private static Pen RoundJoinPen(Color c, float width)
    {
        var key = (c.ToArgb(), width);
        if (PenCache.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(c, width) { LineJoin = LineJoin.Round };
        if (PenCache.Count < MaxCachedPens) PenCache[key] = pen;
        return pen;
    }

    // ---------------- Şekiller ----------------

    // FillRounded / WarningTriangle için tekrar kullanılan yol (her çağrıda yerel GDI+ nesnesi açılıp kapanmasın)
    [ThreadStatic] private static GraphicsPath? _scratch;

    private static GraphicsPath Scratch()
    {
        var p = _scratch ??= new GraphicsPath();
        p.Reset();
        return p;
    }

    /// <summary>Yuvarlak köşeli dikdörtgen yolu. Çağıran dispose eder.</summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        AddRounded(p, r, radius);
        return p;
    }

    private static void AddRounded(GraphicsPath p, RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        var p = Scratch();
        AddRounded(p, r, radius);
        g.FillPath(SharedBrush(c), p);
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
        Color c = color ?? Warning;
        var p = Scratch();
        p.AddPolygon(new[]
        {
            new PointF(r.X + r.Width / 2, r.Y),
            new PointF(r.Right, r.Bottom),
            new PointF(r.X, r.Bottom)
        });
        g.FillPath(SharedBrush(c), p);
        g.DrawPath(RoundJoinPen(c, r.Width * 0.12f), p);

        var dark = SharedBrush(WarningDark);
        float cx = r.X + r.Width / 2, w = r.Width * 0.13f;
        g.FillRectangle(dark, cx - w / 2, r.Y + r.Height * 0.36f, w, r.Height * 0.32f);
        g.FillRectangle(dark, cx - w / 2, r.Y + r.Height * 0.76f, w, w);
    }
}
