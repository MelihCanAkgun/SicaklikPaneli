using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SicaklikPaneli;

/// <summary>
/// Ana pencerenin içeriği. Her şey 860x600 mantıksal piksellik bir tuvale çizilir
/// ve ekran ölçeğine (DPI) göre büyütülür.
/// </summary>
public sealed class DashboardView : Control
{
    public const int LogicalWidth = 860, LogicalHeight = 600;
    private const float TempMin = 30, TempMax = 100;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Snapshot Data { get; set; } = Snapshot.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Snapshot> History { get; set; } = Array.Empty<Snapshot>();
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int HistoryCapacity { get; set; } = 180;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PanelMode Mode { get; set; } = PanelMode.Otomatik;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RtssConnected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RtssInstalled { get; set; }

    public event Action<PanelMode>? ModeSelected;

    private static readonly (PanelMode mode, string text)[] Segments =
    {
        (PanelMode.Otomatik, "Otomatik"), (PanelMode.HerZaman, "Her zaman"), (PanelMode.Kapali, "Kapalı")
    };
    private int _hoverSegment = -1;

    public DashboardView()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
    }

    private float Scale_ => Width / (float)LogicalWidth;

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, Scale_);

    public void Render(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Theme.Background);
        g.ScaleTransform(scale, scale);

        DrawHeader(g);
        DrawDeviceCard(g, new RectangleF(20, 68, 400, 250), "İŞLEMCİ", Data.CpuName, Theme.Cpu,
            Data.CpuTemp, Data.CpuLoad, Data.CpuPower, Data.CpuThrottle,
            "FREKANS", Data.CpuClock is float mhz ? $"{mhz / 1000f:0.00} GHz" : "--", null);

        string vram = Data.GpuMemUsed is float u && Data.GpuMemTotal is float t && t > 0
            ? $"{u / 1024f:0.0} / {t / 1024f:0.0} GB" : "--";
        float? vramPct = Data.GpuMemUsed is float u2 && Data.GpuMemTotal is float t2 && t2 > 0 ? u2 / t2 * 100 : null;
        DrawDeviceCard(g, new RectangleF(440, 68, 400, 250), "EKRAN KARTI", Data.GpuName, Theme.Gpu,
            Data.GpuTemp, Data.GpuLoad, Data.GpuPower, Data.GpuThrottle, "VRAM", vram, vramPct);

        DrawHistoryCard(g, new RectangleF(20, 334, 560, 190));
        DrawRamCard(g, new RectangleF(600, 334, 240, 190));
        DrawFooter(g);
    }

    // ---------------- Üst başlık ----------------

    private void DrawHeader(Graphics g)
    {
        Theme.FillRounded(g, Theme.Accent, new RectangleF(20, 22, 4, 26), 2);
        using var title = Theme.Font(20, true);
        using var tb = new SolidBrush(Theme.Text);
        g.DrawString("SICAKLIK PANELİ", title, tb, 32, 20);

        string status = RtssConnected ? "Oyun içi gösterge etkin"
                      : RtssInstalled ? "Oyun içi gösterge bekleniyor" : "RTSS kurulu değil";
        using var small = Theme.Font(12);
        using var mb = new SolidBrush(Theme.Muted);
        var size = g.MeasureString(status, small);
        float x = LogicalWidth - 20 - size.Width;
        g.DrawString(status, small, mb, x, 27);
        using var dot = new SolidBrush(RtssConnected ? Theme.Ok : Theme.Muted);
        g.FillEllipse(dot, x - 14, 31, 8, 8);
    }

    // ---------------- CPU / GPU kartı ----------------

    private void DrawDeviceCard(Graphics g, RectangleF r, string label, string name, Color color,
        float? temp, float? load, float? power, bool throttle,
        string extraLabel, string extraValue, float? extraPercent)
    {
        DrawCard(g, r, throttle ? Theme.Warning : Theme.CardBorder);

        using var labelFont = Theme.Font(12, true);
        using var nameFont = Theme.Font(12);
        using var muted = new SolidBrush(Theme.Muted);
        using var colorBrush = new SolidBrush(color);
        g.DrawString(label, labelFont, colorBrush, r.X + 18, r.Y + 14);
        var nameRect = new RectangleF(r.X + 18, r.Y + 32, throttle ? r.Width - 190 : r.Width - 36, 18);
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(name, nameFont, muted, nameRect, fmt);

        if (throttle) DrawThrottleBadge(g, r.Right - 16, r.Y + 14);

        // Dairesel sıcaklık göstergesi
        DrawGauge(g, new PointF(r.X + 108, r.Y + 152), 70, temp);

        // Sağ sütun
        float x = r.X + 216, w = r.Width - 216 - 22;
        DrawStat(g, x, r.Y + 72, w, "KULLANIM", load.HasValue ? $"{load.Value:0}%" : "--", load, color);
        DrawStat(g, x, r.Y + 128, w, "GÜÇ", power.HasValue ? $"{power.Value:0.0} W" : "--", null, color);
        DrawStat(g, x, r.Y + 172, w, extraLabel, extraValue, extraPercent, color);
    }

    private static void DrawThrottleBadge(Graphics g, float right, float y)
    {
        const string text = "ISIL KISITLAMA";
        using var font = Theme.Font(11, true);
        float tw = g.MeasureString(text, font).Width;
        var rect = new RectangleF(right - tw - 34, y, tw + 34, 24);
        Theme.FillRounded(g, Color.FromArgb(60, Theme.Warning), rect, 12);
        Theme.WarningTriangle(g, new RectangleF(rect.X + 9, rect.Y + 5.5f, 14, 12.5f));
        using var b = new SolidBrush(Color.FromArgb(255, 150, 150));
        g.DrawString(text, font, b, rect.X + 27, rect.Y + 4);
    }

    private static void DrawGauge(Graphics g, PointF c, float radius, float? temp)
    {
        const float start = 135, sweep = 270, thickness = 11;
        var rect = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        using (var track = new Pen(Theme.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, rect, start, sweep);

        Color color = Theme.TempColor(temp);
        if (temp is float t)
        {
            float frac = Math.Clamp((t - TempMin) / (TempMax - TempMin), 0.02f, 1f);
            using var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, start, sweep * frac);
        }

        using var big = Theme.Font(44, true);
        using var unit = Theme.Font(14);
        using var tb = new SolidBrush(Theme.Text);
        using var mb = new SolidBrush(Theme.Muted);
        string text = temp.HasValue ? temp.Value.ToString("0") : "--";
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, big, tb, new RectangleF(rect.X, rect.Y - 6, rect.Width, rect.Height), fmt);
        g.DrawString("°C", unit, mb, new RectangleF(rect.X, c.Y + 26, rect.Width, 20), fmt);
    }

    private static void DrawStat(Graphics g, float x, float y, float w, string label, string value, float? percent, Color color)
    {
        using var labelFont = Theme.Font(11, true);
        using var valueFont = Theme.Font(16, true);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(label, labelFont, muted, x, y + 4);
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(value, valueFont, text, new RectangleF(x, y, w, 24), right);
        if (percent.HasValue || label == "KULLANIM")
            Theme.Bar(g, new RectangleF(x + 2, y + 28, w - 4, 6), percent, color);
    }

    // ---------------- Sıcaklık geçmişi ----------------

    private void DrawHistoryCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r, Theme.CardBorder);
        using var labelFont = Theme.Font(12, true);
        using var small = Theme.Font(11);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString("SICAKLIK GEÇMİŞİ", labelFont, muted, r.X + 18, r.Y + 14);

        // Açıklama (sağ üst)
        float lx = r.Right - 18;
        lx = Legend(g, small, "GPU", Theme.Gpu, lx, r.Y + 14);
        Legend(g, small, "CPU", Theme.Cpu, lx - 14, r.Y + 14);

        var plot = new RectangleF(r.X + 50, r.Y + 44, r.Width - 50 - 20, r.Height - 44 - 30);
        using var grid = new Pen(Color.FromArgb(40, 40, 48), 1);
        using var axisFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        foreach (int t in new[] { 40, 60, 80, 100 })
        {
            float y = plot.Bottom - (t - TempMin) / (TempMax - TempMin) * plot.Height;
            g.DrawLine(grid, plot.X, y, plot.Right, y);
            g.DrawString($"{t}°", small, muted, new RectangleF(r.X + 8, y - 8, 36, 16), axisFmt);
        }

        int minutes = Math.Max(1, HistoryCapacity / 60);
        g.DrawString($"{minutes} dk önce", small, muted, plot.X - 2, plot.Bottom + 6);
        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("şimdi", small, muted, new RectangleF(plot.Right - 60, plot.Bottom + 6, 62, 16), far);

        DrawSeries(g, plot, s => s.GpuTemp, Theme.Gpu);
        DrawSeries(g, plot, s => s.CpuTemp, Theme.Cpu);
    }

    private static float Legend(Graphics g, Font font, string text, Color color, float right, float y)
    {
        float w = g.MeasureString(text, font).Width;
        using var mb = new SolidBrush(Theme.Text);
        g.DrawString(text, font, mb, right - w, y);
        using var cb = new SolidBrush(color);
        g.FillEllipse(cb, right - w - 12, y + 4, 8, 8);
        return right - w - 12;
    }

    private void DrawSeries(Graphics g, RectangleF plot, Func<Snapshot, float?> pick, Color color)
    {
        if (History.Count < 2) return;
        int cap = Math.Max(HistoryCapacity, 2);
        var points = new List<PointF>();
        int offset = cap - History.Count; // en yeni değer sağ kenarda
        for (int i = 0; i < History.Count; i++)
        {
            if (pick(History[i]) is not float v) continue;
            float x = plot.X + (offset + i) / (float)(cap - 1) * plot.Width;
            float y = plot.Bottom - (Math.Clamp(v, TempMin, TempMax) - TempMin) / (TempMax - TempMin) * plot.Height;
            points.Add(new PointF(x, y));
        }
        if (points.Count < 2) return;
        using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points.ToArray());
        var last = points[^1];
        using var b = new SolidBrush(color);
        g.FillEllipse(b, last.X - 3.5f, last.Y - 3.5f, 7, 7);
    }

    // ---------------- Bellek ----------------

    private void DrawRamCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r, Theme.CardBorder);
        using var labelFont = Theme.Font(12, true);
        using var big = Theme.Font(34, true);
        using var mid = Theme.Font(13);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString("BELLEK", labelFont, muted, r.X + 18, r.Y + 14);

        float? pct = Data.RamUsed is float u && Data.RamTotal is float t && t > 0 ? u / t * 100 : null;
        string used = Data.RamUsed.HasValue ? Data.RamUsed.Value.ToString("0.0") : "--";
        g.DrawString(used, big, text, r.X + 14, r.Y + 52);
        float uw = g.MeasureString(used, big).Width;
        g.DrawString("GB", mid, muted, r.X + 10 + uw, r.Y + 74);

        Theme.Bar(g, new RectangleF(r.X + 20, r.Y + 118, r.Width - 40, 8), pct, Color.FromArgb(176, 176, 190));
        string total = Data.RamTotal.HasValue ? $"Toplam {Data.RamTotal.Value:0.0} GB" : "";
        g.DrawString(total, mid, muted, r.X + 18, r.Y + 138);
        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(pct.HasValue ? $"{pct.Value:0}%" : "", mid, text, new RectangleF(r.X, r.Y + 138, r.Width - 18, 20), far);
    }

    // ---------------- Alt satır: oyun paneli modu ----------------

    private RectangleF SegmentRect(int i) => new(150 + i * 112, 544, 108, 34);

    private void DrawFooter(Graphics g)
    {
        using var labelFont = Theme.Font(12, true);
        using var font = Theme.Font(13);
        using var bold = Theme.Font(13, true);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString("OYUN PANELİ", labelFont, muted, 22, 553);

        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < Segments.Length; i++)
        {
            var rect = SegmentRect(i);
            bool active = Segments[i].mode == Mode;
            Color fill = active ? Theme.Accent : i == _hoverSegment ? Color.FromArgb(44, 44, 52) : Theme.Card;
            Theme.FillRounded(g, fill, rect, 8);
            if (!active)
            {
                using var path = Theme.RoundedRect(rect, 8);
                using var pen = new Pen(Theme.CardBorder);
                g.DrawPath(pen, path);
            }
            g.DrawString(Segments[i].text, active ? bold : font, active ? Brushes.White : text, rect, fmt);
        }

        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("Kısayol: Ctrl + Shift + F10", font, muted, new RectangleF(540, 552, 300, 20), far);
    }

    private static void DrawCard(Graphics g, RectangleF r, Color border)
    {
        Theme.FillRounded(g, Theme.Card, r, 12);
        using var path = Theme.RoundedRect(r, 12);
        using var pen = new Pen(border, 1);
        g.DrawPath(pen, path);
    }

    // ---------------- Fare ----------------

    private int HitSegment(Point p)
    {
        var lp = new PointF(p.X / Scale_, p.Y / Scale_);
        for (int i = 0; i < Segments.Length; i++)
            if (SegmentRect(i).Contains(lp)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitSegment(e.Location);
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != _hoverSegment) { _hoverSegment = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverSegment != -1) { _hoverSegment = -1; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = HitSegment(e.Location);
        if (h >= 0) ModeSelected?.Invoke(Segments[h].mode);
    }
}
