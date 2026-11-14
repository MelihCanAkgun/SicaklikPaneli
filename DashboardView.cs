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
        using var nameFont = Theme.Font(12);
        using var muted = new SolidBrush(Theme.Muted);
        var nameRect = new RectangleF(r.X + 18, r.Y + 32, throttle ? r.Width - 190 : r.Width - 36, 18);
        using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(name, nameFont, muted, nameRect, fmt);

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

        if (temp is float t)
        {
            float frac = Math.Clamp((t - TempMin) / (TempMax - TempMin), 0.02f, 1f);
            using var pen = new Pen(Theme.TempColor(temp), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, start, sweep * frac);
        }

        using var big = Theme.Number(46);
        using var unit = Theme.Font(14);
        using var tb = new SolidBrush(Theme.Text);
        using var mb = new SolidBrush(Theme.Muted);
        string text = temp.HasValue ? temp.Value.ToString("0") : "--";
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, big, tb, new RectangleF(rect.X, rect.Y - 6, rect.Width, rect.Height), fmt);
        g.DrawString("°C", unit, mb, new RectangleF(rect.X, c.Y + 26, rect.Width, 20), fmt);
    }

    private static void DrawStat(Graphics g, float x, float y, float w, string label, string value,
        float? percent, Color color, bool withBar)
    {
        using var labelFont = Theme.Font(12);
        using var valueFont = Theme.Number(17);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(label, labelFont, muted, x, y + Theme.Ascent(valueFont) - Theme.Ascent(labelFont));
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(value, valueFont, text, new RectangleF(x, y, w, 24), right);
        if (withBar) Theme.Bar(g, new RectangleF(x + 2, y + 28, w - 4, 6), percent, color);
    }

    // ---------------- Sıcaklık geçmişi ----------------

    private void DrawHistoryCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r);
        DrawLabel(g, "Sıcaklık geçmişi", r.X + 18, r.Y + 14);
        using var small = Theme.Font(11);
        using var muted = new SolidBrush(Theme.Muted);

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
        DrawCard(g, r);
        DrawLabel(g, "Bellek", r.X + 18, r.Y + 14);
        using var big = Theme.Number(36);
        using var mid = Theme.Font(13);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);

        float? pct = Data.RamUsed is float u && Data.RamTotal is float t && t > 0 ? u / t * 100 : null;
        string used = Data.RamUsed.HasValue ? Data.RamUsed.Value.ToString("0.0") : "--";
        g.DrawString(used, big, text, r.X + 14, r.Y + 52);
        float uw = g.MeasureString(used, big).Width;
        g.DrawString("GB", mid, muted, r.X + 10 + uw, r.Y + 52 + Theme.Ascent(big) - Theme.Ascent(mid));

        Theme.Bar(g, new RectangleF(r.X + 20, r.Y + 118, r.Width - 40, 8), pct, Color.FromArgb(176, 176, 190));
        string total = Data.RamTotal.HasValue ? $"Toplam {Data.RamTotal.Value:0.0} GB" : "";
        g.DrawString(total, mid, muted, r.X + 18, r.Y + 138);
        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(pct.HasValue ? $"{pct.Value:0}%" : "", mid, text, new RectangleF(r.X, r.Y + 138, r.Width - 18, 20), far);
    }

    // ---------------- Alt satır: oyun paneli modu ----------------

    private void DrawFooter(Graphics g)
    {
        // Performans modu (MSI Shift)
        var fan = Data.Fan;
        bool canSwitch = fan.Available && fan.Perf != PerfMode.Bilinmiyor;
        DrawLabel(g, "Performans", 22, 553);
        Button(g, new RectangleF(124, 544, 92, 34), "Turbo", fan.Perf == PerfMode.Turbo,
            () => PerfSelected?.Invoke(PerfMode.Turbo), enabled: canSwitch);
        Button(g, new RectangleF(220, 544, 92, 34), "Dengeli", fan.Perf == PerfMode.Dengeli,
            () => PerfSelected?.Invoke(PerfMode.Dengeli), enabled: canSwitch, activeColor: Color.FromArgb(52, 96, 150));
        if (fan.Perf is PerfMode.Sport or PerfMode.Eco)
        {
            using var font = Theme.Font(12);
            using var muted = new SolidBrush(Theme.Muted);
            g.DrawString($"şu an: {fan.Perf}", font, muted, 320, 553);
        }

        // Oyun paneli modu
        DrawLabel(g, "Oyun paneli", 448, 553);
        for (int i = 0; i < Segments.Length; i++)
        {
            var (mode, text) = Segments[i];
            Button(g, new RectangleF(548 + i * 98, 544, 94, 34), text, mode == Mode, () => ModeSelected?.Invoke(mode));
        }
    }
}
