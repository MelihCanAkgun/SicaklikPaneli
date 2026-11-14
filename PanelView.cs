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

    protected PanelView()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
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

    // ---------------- Ortak çizimler ----------------

    private void DrawHeader(Graphics g, float scale)
    {
        // Logo: piksel sanatı bulanıklaşmasın diye ekranda tam sayı katında ve piksel ızgarasına oturtulmuş çizilir
        int k = Math.Max(2, (int)(56 * scale / 32));
        float logo = k * 32 / scale;
        // Çizimin üstteki 4 satırı boş; dolu kısmı başlık satırının ortasına (y=35) gelsin
        float lx = MathF.Round(14 * scale) / scale, ly = MathF.Round((35 - logo * 18 / 32) * scale) / scale;
        DragonLogo.Draw(g, new RectangleF(lx, ly, logo, logo));

        using var title = Theme.Font(22, true);
        using var tb = new SolidBrush(Theme.Text);
        g.DrawString("Dragon Saj", title, tb, lx + logo + 6, 20);

        // Sekmeler
        using var tabFont = Theme.Font(14, true);
        using var muted = new SolidBrush(Theme.Muted);
        float x = 262;
        for (int i = 0; i < Tabs.Length; i++)
        {
            float w = g.MeasureString(Tabs[i], tabFont).Width;
            var rect = new RectangleF(x - 8, 16, w + 16, 38);
            bool active = i == ActiveTab;
            g.DrawString(Tabs[i], tabFont, active || Hot(rect) ? tb : muted, x, 27);
            if (active) Theme.FillRounded(g, Theme.Accent, new RectangleF(x + 2, 49, w - 4, 3), 1.5f);
            int index = i;
            Hit(rect, () => TabSelected?.Invoke(index));
            x += w + 26;
        }

        string status = RtssConnected ? "Oyun içi gösterge etkin"
                      : RtssInstalled ? "Oyun içi gösterge bekleniyor" : "RTSS kurulu değil";
        using var small = Theme.Font(12);
        var size = g.MeasureString(status, small);
        float sx = LogicalWidth - 20 - size.Width;
        g.DrawString(status, small, muted, sx, 27);
        using var dot = new SolidBrush(RtssConnected ? Theme.Ok : Theme.Muted);
        g.FillEllipse(dot, sx - 14, 31, 8, 8);
    }

    protected static void DrawCard(Graphics g, RectangleF r, Color? border = null)
    {
        Theme.FillRounded(g, Theme.Card, r, 12);
        using var path = Theme.RoundedRect(r, 12);
        using var pen = new Pen(border ?? Theme.CardBorder, 1);
        g.DrawPath(pen, path);
    }

    protected static void DrawLabel(Graphics g, string text, float x, float y, Color? color = null)
    {
        using var font = Theme.Font(12, true);
        using var b = new SolidBrush(color ?? Theme.Muted);
        g.DrawString(text, font, b, x, y);
    }

    /// <summary>Düğme çizer ve tıklama alanını kaydeder. active: seçili (dolu) görünüm.</summary>
    protected void Button(Graphics g, RectangleF rect, string text, bool active, Action onClick,
        bool enabled = true, Color? activeColor = null, float fontSize = 13)
    {
        Color fill = active ? activeColor ?? Theme.Accent
                   : enabled && Hot(rect) ? Color.FromArgb(46, 46, 55) : Color.FromArgb(33, 33, 39);
        Theme.FillRounded(g, fill, rect, 8);
        if (!active)
        {
            using var path = Theme.RoundedRect(rect, 8);
            using var pen = new Pen(Theme.CardBorder);
            g.DrawPath(pen, path);
        }
        using var font = Theme.Font(fontSize, active);
        using var brush = new SolidBrush(active ? Color.White : enabled ? Theme.Text : Color.FromArgb(96, 96, 106));
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, fmt);
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

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitIndex(ToLogical(e.Location));
        if (i >= 0) { var action = _hits[i].action; action(); Invalidate(); }
    }
}
