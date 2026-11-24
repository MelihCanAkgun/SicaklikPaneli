using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SicaklikPaneli;

/// <summary>
/// Ana penceredeki sayfaların ortak tabanı. Her şey 860x600 mantıksal piksellik bir tuvale çizilir
/// ve ekran ölçeğine (DPI) göre büyütülür. Tıklanabilir alanlar çizim sırasında kaydedilir.
/// </summary>
public abstract class PanelView : Control
{
    public const int LogicalWidth = 860, LogicalHeight = 600;
    public static readonly string[] Tabs = { "Genel", "Fanlar", "Performans" };

    public int ActiveTab;
    public bool RtssConnected, RtssInstalled;
    public event Action<int>? TabSelected;

    private readonly List<(RectangleF rect, Action action)> _hits = new();
    private PointF _mouse = new(-1, -1);
    private int _hotIndex = -1;
    private readonly Action[] _tabActions;

    // Çizim nesneleri her karede yeniden oluşturulmasın diye bir kez oluşturulup saklanır.
    // Yazı tipleri piksel biriminde olduğundan ölçekten bağımsızdır; hepsi yalnızca arayüz
    // iş parçacığında kullanılır ve program boyunca yaşar.
    private static readonly Dictionary<(float px, bool semibold, bool number), Font> s_fonts = new();
    private static readonly Dictionary<int, SolidBrush> s_brushes = new();
    private static readonly Dictionary<int, Pen> s_pens = new();
    private static readonly StringFormat s_center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    protected PanelView()
    {
        // Her kare Render içinde tamamen boyanır: arka plan silmeye gerek yok (Opaque),
        // titreme olmasın diye çift tampon, ölçek genişliğe bağlı olduğu için boyut değişince tümü yeniden çizilir.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;

        _tabActions = new Action[Tabs.Length];
        for (int i = 0; i < Tabs.Length; i++)
        {
            int index = i;
            _tabActions[i] = () => TabSelected?.Invoke(index);
        }
    }

    protected float ViewScale => Width / (float)LogicalWidth;
    protected PointF ToLogical(Point p) => new(p.X / ViewScale, p.Y / ViewScale);
    protected PointF MouseLogical => _mouse;

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ViewScale);

    public void Render(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);
        g.ScaleTransform(scale, scale);
        _hits.Clear();
        DrawHeader(g, scale);
        DrawContent(g);
    }

    protected abstract void DrawContent(Graphics g);

    // ---------------- Önbellekli çizim nesneleri ----------------

    /// <summary>Theme.Font ile aynı yazı tipi; önbellekten gelir, Dispose EDİLMEMELİ.</summary>
    private protected static Font CachedFont(float px, bool semibold = false) => GetFont(px, semibold, false);

    /// <summary>Theme.Number ile aynı yazı tipi; önbellekten gelir, Dispose EDİLMEMELİ.</summary>
    private protected static Font CachedNumber(float px) => GetFont(px, false, true);

    private static Font GetFont(float px, bool semibold, bool number)
    {
        var key = (px, semibold, number);
        if (!s_fonts.TryGetValue(key, out var f))
            s_fonts[key] = f = number ? Theme.Number(px) : Theme.Font(px, semibold);
        return f;
    }

    /// <summary>Renge göre önbellekli fırça; Dispose EDİLMEMELİ.</summary>
    private protected static SolidBrush CachedBrush(Color c)
    {
        int key = c.ToArgb();
        if (!s_brushes.TryGetValue(key, out var b)) s_brushes[key] = b = new SolidBrush(c);
        return b;
    }

    /// <summary>Renge göre önbellekli 1 piksellik kalem; Dispose EDİLMEMELİ.</summary>
    private protected static Pen CachedPen(Color c)
    {
        int key = c.ToArgb();
        if (!s_pens.TryGetValue(key, out var p)) s_pens[key] = p = new Pen(c, 1);
        return p;
    }

    // ---------------- Ortak çizimler ----------------

    private void DrawHeader(Graphics g, float scale)
    {
        // Logo: piksel sanatı bulanıklaşmasın diye ekranda tam sayı katında ve piksel ızgarasına oturtulmuş çizilir
        int k = Math.Max(2, (int)(56 * scale / 32));
        float logo = k * 32 / scale;
        // Çizimin üstteki 4 satırı boş; dolu kısmı başlık satırının ortasına (y=35) gelsin
        float lx = MathF.Round(14 * scale) / scale, ly = MathF.Round((35 - logo * 18 / 32) * scale) / scale;
        DragonLogo.Draw(g, new RectangleF(lx, ly, logo, logo));

        var tb = CachedBrush(Theme.Text);
        g.DrawString("Dragon Saj", CachedFont(22, true), tb, lx + logo + 6, 20);

        // Sekmeler
        var tabFont = CachedFont(14, true);
        var muted = CachedBrush(Theme.Muted);
        float x = 262;
        for (int i = 0; i < Tabs.Length; i++)
        {
            float w = g.MeasureString(Tabs[i], tabFont).Width;
            var rect = new RectangleF(x - 8, 16, w + 16, 38);
            bool active = i == ActiveTab;
            g.DrawString(Tabs[i], tabFont, active || Hot(rect) ? tb : muted, x, 27);
            if (active) Theme.FillRounded(g, Theme.Accent, new RectangleF(x + 2, 49, w - 4, 3), 1.5f);
            Hit(rect, _tabActions[i]);
            x += w + 26;
        }

        string status = RtssConnected ? "Oyun içi gösterge etkin"
                      : RtssInstalled ? "Oyun içi gösterge bekleniyor" : "RTSS kurulu değil";
        var small = CachedFont(12);
        var size = g.MeasureString(status, small);
        float sx = LogicalWidth - 20 - size.Width;
        g.DrawString(status, small, muted, sx, 27);
        g.FillEllipse(CachedBrush(RtssConnected ? Theme.Ok : Theme.Muted), sx - 14, 31, 8, 8);
    }

    protected static void DrawCard(Graphics g, RectangleF r, Color? border = null)
    {
        // Dolgu ve kenar aynı yolu kullanır; yol bir kez oluşturulur
        using var path = Theme.RoundedRect(r, 12);
        g.FillPath(CachedBrush(Theme.Card), path);
        g.DrawPath(CachedPen(border ?? Theme.CardBorder), path);
    }

    protected static void DrawLabel(Graphics g, string text, float x, float y, Color? color = null)
    {
        g.DrawString(text, CachedFont(12, true), CachedBrush(color ?? Theme.Muted), x, y);
    }

    /// <summary>Düğme çizer ve tıklama alanını kaydeder. active: seçili (dolu) görünüm.</summary>
    protected void Button(Graphics g, RectangleF rect, string text, bool active, Action onClick,
        bool enabled = true, Color? activeColor = null, float fontSize = 13)
    {
        Color fill = active ? activeColor ?? Theme.Accent
                   : enabled && Hot(rect) ? Color.FromArgb(46, 46, 55) : Color.FromArgb(33, 33, 39);
        using (var path = Theme.RoundedRect(rect, 8))
        {
            g.FillPath(CachedBrush(fill), path);
            if (!active) g.DrawPath(CachedPen(Theme.CardBorder), path);
        }
        var brush = CachedBrush(active ? Color.White : enabled ? Theme.Text : Color.FromArgb(96, 96, 106));
        g.DrawString(text, CachedFont(fontSize, active), brush, rect, s_center);
        if (enabled) Hit(rect, onClick);
    }

    // ---------------- Tıklama alanları ----------------

    protected void Hit(RectangleF rect, Action action) => _hits.Add((rect, action));
    protected bool Hot(RectangleF rect) => rect.Contains(_mouse);

    private int HitIndex(PointF p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].rect.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = ToLogical(e.Location);
        int hot = HitIndex(_mouse);
        if (!HandlesCursor) Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        // Yalnızca fareyle üzerine gelinen hedef değişince yeniden çiz
        if (hot != _hotIndex) { _hotIndex = hot; Invalidate(); }
    }

    /// <summary>Alt sınıf imleci kendisi yönetiyorsa (ör. sürükleme) true döndürür.</summary>
    protected virtual bool HandlesCursor => false;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new PointF(-1, -1);
        if (_hotIndex != -1) { _hotIndex = -1; Invalidate(); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Sayfa gizlenirken eski fare konumu kalmasın; yeniden açıldığında ilk karede
        // yanlış düğme/sekme vurgulu görünmesin (fare hareket edince zaten güncellenir)
        if (!Visible) { _mouse = new PointF(-1, -1); _hotIndex = -1; }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitIndex(ToLogical(e.Location));
        if (i >= 0) { var action = _hits[i].action; action(); Invalidate(); }
    }
}
