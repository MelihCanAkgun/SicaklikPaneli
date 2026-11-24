using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>"Genel" sayfası: işlemci, ekran kartı, sıcaklık geçmişi ve bellek.</summary>
public sealed class DashboardView : PanelView
{
    private const float TempMin = 30, TempMax = 100;

    public Snapshot Data = Snapshot.Empty;
    public IReadOnlyList<Snapshot> History = Array.Empty<Snapshot>();
    public int HistoryCapacity = 180;
    public PanelMode Mode = PanelMode.Otomatik;

    public event Action<PanelMode>? ModeSelected;
    public event Action<PerfMode>? PerfSelected;

    private static readonly (PanelMode mode, string text)[] Segments =
    {
        (PanelMode.Otomatik, "Otomatik"), (PanelMode.HerZaman, "Her zaman"), (PanelMode.Kapali, "Kapalı")
    };

    private static readonly int[] GridTemps = { 40, 60, 80, 100 };
    private static readonly string[] GridLabels = { "40°", "60°", "80°", "100°" };

    // Her karede yeniden oluşturulmayan çizim nesneleri (yalnızca arayüz iş parçacığında kullanılır)
    private static readonly StringFormat EllipsisFormat = new() { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private static readonly StringFormat FarFormat = new() { Alignment = StringAlignment.Far };
    private static readonly StringFormat AxisFormat = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
    private static readonly Pen TrackPen = new(Theme.Track, 11) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    private static readonly Pen GridPen = new(Color.FromArgb(40, 40, 48), 1);
    private static readonly Pen CpuSeriesPen = new(Theme.Cpu, 2f) { LineJoin = LineJoin.Round };
    private static readonly Pen GpuSeriesPen = new(Theme.Gpu, 2f) { LineJoin = LineJoin.Round };

    // Farklı yazı tiplerini aynı taban çizgisine oturtan sabit kaydırmalar
    private static readonly float StatLabelOffset = Theme.Ascent(CachedNumber(17)) - Theme.Ascent(CachedFont(12));
    private static readonly float RamUnitOffset = Theme.Ascent(CachedNumber(36)) - Theme.Ascent(CachedFont(13));

    // Geçmiş grafiği noktaları için yeniden kullanılan tampon
    private PointF[] _points = Array.Empty<PointF>();

    // Düğme eylemleri her karede yeni kapanış (closure) üretmesin diye bir kez oluşturulur
    private readonly Action _turbo, _balanced;
    private readonly Action[] _segmentActions;

    public DashboardView()
    {
        _turbo = () => PerfSelected?.Invoke(PerfMode.Turbo);
        _balanced = () => PerfSelected?.Invoke(PerfMode.Dengeli);
        _segmentActions = new Action[Segments.Length];
        for (int i = 0; i < Segments.Length; i++)
        {
            var mode = Segments[i].mode;
            _segmentActions[i] = () => ModeSelected?.Invoke(mode);
        }
    }

    protected override void DrawContent(Graphics g)
    {
        var fan = Data.Fan;
        DrawDeviceCard(g, new RectangleF(20, 68, 400, 250), "İşlemci", Data.CpuName, Theme.Cpu,
            Data.CpuTemp, Data.CpuLoad, Data.CpuPower, Data.CpuThrottle,
            "Frekans", Data.CpuClock is float mhz ? $"{mhz / 1000f:0.00} GHz" : "--", null,
            fan.Available ? fan.CpuRpm : null);

        string vram = Data.GpuMemUsed is float u && Data.GpuMemTotal is float t && t > 0
            ? $"{u / 1024f:0.0} / {t / 1024f:0.0} GB" : "--";
        float? vramPct = Data.GpuMemUsed is float u2 && Data.GpuMemTotal is float t2 && t2 > 0 ? u2 / t2 * 100 : null;
        DrawDeviceCard(g, new RectangleF(440, 68, 400, 250), "Ekran kartı", Data.GpuName, Theme.Gpu,
            Data.GpuTemp, Data.GpuLoad, Data.GpuPower, Data.GpuThrottle, "VRAM", vram, vramPct,
            fan.Available ? fan.GpuRpm : null);

        DrawHistoryCard(g, new RectangleF(20, 334, 560, 190));
        DrawRamCard(g, new RectangleF(600, 334, 240, 190));
        DrawFooter(g);
    }

    // ---------------- CPU / GPU kartı ----------------

    private void DrawDeviceCard(Graphics g, RectangleF r, string label, string name, Color color,
        float? temp, float? load, float? power, bool throttle,
        string extraLabel, string extraValue, float? extraPercent, int? fanRpm)
    {
        DrawCard(g, r, throttle ? Theme.Warning : null);

        DrawLabel(g, label, r.X + 18, r.Y + 14, color);
        var nameRect = new RectangleF(r.X + 18, r.Y + 32, throttle ? r.Width - 190 : r.Width - 36, 18);
        g.DrawString(name, CachedFont(12), CachedBrush(Theme.Muted), nameRect, EllipsisFormat);

        if (throttle) DrawThrottleBadge(g, r.Right - 16, r.Y + 14);

        DrawGauge(g, new PointF(r.X + 108, r.Y + 152), 70, temp);

        // Sağ sütun
        float x = r.X + 216, w = r.Width - 216 - 22;
        DrawStat(g, x, r.Y + 62, w, "Kullanım", load.HasValue ? $"{load.Value:0}%" : "--", load, color, true);
        DrawStat(g, x, r.Y + 108, w, "Güç", power.HasValue ? $"{power.Value:0.0} W" : "--", null, color, false);
        DrawStat(g, x, r.Y + 142, w, extraLabel, extraValue, extraPercent, color, extraPercent.HasValue);
        DrawStat(g, x, r.Y + (extraPercent.HasValue ? 188 : 176), w, "Fan",
            fanRpm is int rpm ? (rpm > 0 ? $"{rpm} RPM" : "Duruyor") : "--", null, color, false);
    }

    private static void DrawThrottleBadge(Graphics g, float right, float y)
    {
        const string text = "Isıl kısıtlama";
        var font = CachedFont(11, true);
        float tw = g.MeasureString(text, font).Width;
        var rect = new RectangleF(right - tw - 34, y, tw + 34, 24);
        Theme.FillRounded(g, Color.FromArgb(60, Theme.Warning), rect, 12);
        Theme.WarningTriangle(g, new RectangleF(rect.X + 9, rect.Y + 5.5f, 14, 12.5f));
        g.DrawString(text, font, CachedBrush(Color.FromArgb(255, 150, 150)), rect.X + 27, rect.Y + 4);
    }

    private static void DrawGauge(Graphics g, PointF c, float radius, float? temp)
    {
        const float start = 135, sweep = 270, thickness = 11;
        var rect = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        g.DrawArc(TrackPen, rect, start, sweep);

        if (temp is float t)
        {
            float frac = Math.Clamp((t - TempMin) / (TempMax - TempMin), 0.02f, 1f);
            using var pen = new Pen(Theme.TempColor(temp), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, start, sweep * frac);
        }

        string text = temp.HasValue ? temp.Value.ToString("0") : "--";
        g.DrawString(text, CachedNumber(46), CachedBrush(Theme.Text), new RectangleF(rect.X, rect.Y - 6, rect.Width, rect.Height), CenterFormat);
        g.DrawString("°C", CachedFont(14), CachedBrush(Theme.Muted), new RectangleF(rect.X, c.Y + 26, rect.Width, 20), CenterFormat);
    }

    private static void DrawStat(Graphics g, float x, float y, float w, string label, string value,
        float? percent, Color color, bool withBar)
    {
        g.DrawString(label, CachedFont(12), CachedBrush(Theme.Muted), x, y + StatLabelOffset);
        g.DrawString(value, CachedNumber(17), CachedBrush(Theme.Text), new RectangleF(x, y, w, 24), FarFormat);
        if (withBar) Theme.Bar(g, new RectangleF(x + 2, y + 28, w - 4, 6), percent, color);
    }

    // ---------------- Sıcaklık geçmişi ----------------

    private void DrawHistoryCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r);
        DrawLabel(g, "Sıcaklık geçmişi", r.X + 18, r.Y + 14);
        var small = CachedFont(11);
        var muted = CachedBrush(Theme.Muted);

        float lx = r.Right - 18;
        lx = Legend(g, small, "GPU", Theme.Gpu, lx, r.Y + 14);
        Legend(g, small, "CPU", Theme.Cpu, lx - 14, r.Y + 14);

        var plot = new RectangleF(r.X + 50, r.Y + 44, r.Width - 50 - 20, r.Height - 44 - 30);
        for (int i = 0; i < GridTemps.Length; i++)
        {
            float y = plot.Bottom - (GridTemps[i] - TempMin) / (TempMax - TempMin) * plot.Height;
            g.DrawLine(GridPen, plot.X, y, plot.Right, y);
            g.DrawString(GridLabels[i], small, muted, new RectangleF(r.X + 8, y - 8, 36, 16), AxisFormat);
        }

        int minutes = Math.Max(1, HistoryCapacity / 60);
        g.DrawString($"{minutes} dk önce", small, muted, plot.X - 2, plot.Bottom + 6);
        g.DrawString("şimdi", small, muted, new RectangleF(plot.Right - 60, plot.Bottom + 6, 62, 16), FarFormat);

        DrawSeries(g, plot, gpu: true, GpuSeriesPen, Theme.Gpu);
        DrawSeries(g, plot, gpu: false, CpuSeriesPen, Theme.Cpu);
    }

    private static float Legend(Graphics g, Font font, string text, Color color, float right, float y)
    {
        float w = g.MeasureString(text, font).Width;
        g.DrawString(text, font, CachedBrush(Theme.Text), right - w, y);
        g.FillEllipse(CachedBrush(color), right - w - 12, y + 4, 8, 8);
        return right - w - 12;
    }

    private void DrawSeries(Graphics g, RectangleF plot, bool gpu, Pen pen, Color color)
    {
        var history = History;
        int count = history.Count;
        if (count < 2) return;
        int cap = Math.Max(HistoryCapacity, 2);
        if (_points.Length < count) _points = new PointF[Math.Max(count, cap)];
        var points = _points;
        int n = 0;
        int offset = cap - count; // en yeni değer sağ kenarda
        for (int i = 0; i < count; i++)
        {
            var s = history[i];
            if ((gpu ? s.GpuTemp : s.CpuTemp) is not float v) continue;
            float x = plot.X + (offset + i) / (float)(cap - 1) * plot.Width;
            float y = plot.Bottom - (Math.Clamp(v, TempMin, TempMax) - TempMin) / (TempMax - TempMin) * plot.Height;
            points[n++] = new PointF(x, y);
        }
        if (n < 2) return;
        g.DrawLines(pen, points.AsSpan(0, n));
        var last = points[n - 1];
        g.FillEllipse(CachedBrush(color), last.X - 3.5f, last.Y - 3.5f, 7, 7);
    }

    // ---------------- Bellek ----------------

    private void DrawRamCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r);
        DrawLabel(g, "Bellek", r.X + 18, r.Y + 14);
        var big = CachedNumber(36);
        var mid = CachedFont(13);
        var muted = CachedBrush(Theme.Muted);
        var text = CachedBrush(Theme.Text);

        float? pct = Data.RamUsed is float u && Data.RamTotal is float t && t > 0 ? u / t * 100 : null;
        string used = Data.RamUsed.HasValue ? Data.RamUsed.Value.ToString("0.0") : "--";
        g.DrawString(used, big, text, r.X + 14, r.Y + 52);
        float uw = g.MeasureString(used, big).Width;
        g.DrawString("GB", mid, muted, r.X + 10 + uw, r.Y + 52 + RamUnitOffset);

        Theme.Bar(g, new RectangleF(r.X + 20, r.Y + 118, r.Width - 40, 8), pct, Color.FromArgb(176, 176, 190));
        string total = Data.RamTotal.HasValue ? $"Toplam {Data.RamTotal.Value:0.0} GB" : "";
        g.DrawString(total, mid, muted, r.X + 18, r.Y + 138);
        g.DrawString(pct.HasValue ? $"{pct.Value:0}%" : "", mid, text, new RectangleF(r.X, r.Y + 138, r.Width - 18, 20), FarFormat);
    }

    // ---------------- Alt satır: oyun paneli modu ----------------

    private void DrawFooter(Graphics g)
    {
        // Performans modu (MSI Shift)
        var fan = Data.Fan;
        bool canSwitch = fan.Available && fan.Perf != PerfMode.Bilinmiyor;
        DrawLabel(g, "Performans", 22, 553);
        Button(g, new RectangleF(124, 544, 92, 34), "Turbo", fan.Perf == PerfMode.Turbo, _turbo, enabled: canSwitch);
        Button(g, new RectangleF(220, 544, 92, 34), "Dengeli", fan.Perf == PerfMode.Dengeli, _balanced,
            enabled: canSwitch, activeColor: Color.FromArgb(52, 96, 150));
        if (fan.Perf is PerfMode.Sport or PerfMode.Eco)
            g.DrawString($"şu an: {fan.Perf}", CachedFont(12), CachedBrush(Theme.Muted), 320, 553);

        // Oyun paneli modu
        DrawLabel(g, "Oyun paneli", 448, 553);
        for (int i = 0; i < Segments.Length; i++)
            Button(g, new RectangleF(548 + i * 98, 544, 94, 34), Segments[i].text, Segments[i].mode == Mode, _segmentActions[i]);
    }
}
