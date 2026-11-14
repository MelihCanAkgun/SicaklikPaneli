namespace SicaklikPaneli;

/// <summary>"Performans" sayfası: performans modu ve ekran kartı hız aşırtma.</summary>
public sealed class PerfView : PanelView
{
    // Güvenli tarafta kalmak için sürücünün izin verdiğinden çok daha dar sınırlar
    public const int CoreLimit = 150, MemoryLimit = 300, CoreStep = 25, MemoryStep = 50;

    public Snapshot Data = Snapshot.Empty;
    public OcState Oc = OcState.Unavailable("Ekran kartı bilgisi okunuyor…");
    public bool OcEnabled;
    public int CoreTarget, MemoryTarget;
    public string Message = "";

    public event Action<PerfMode>? PerfSelected;
    /// <summary>(etkin, çekirdek MHz, bellek MHz)</summary>
    public event Action<bool, int, int>? OcRequested;

    private static readonly (string name, int core, int memory)[] Presets =
    {
        ("Hafif", 50, 100), ("Orta", 100, 200), ("Yüksek", 150, 300)
    };

    protected override void DrawContent(Graphics g)
    {
        DrawModeCard(g, new RectangleF(20, 68, 820, 158));
        DrawOcCard(g, new RectangleF(20, 242, 820, 290));

        using var font = Theme.Font(13);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(Message, font, muted, new RectangleF(22, 548, 816, 22));
    }

    // ---------------- Performans modu ----------------

    private void DrawModeCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r);
        DrawLabel(g, "Performans modu", r.X + 18, r.Y + 14);
        var fan = Data.Fan;
        bool canSwitch = fan.Available && fan.Perf != PerfMode.Bilinmiyor;

        float w = (r.Width - 36 - 12) / 2;
        DrawModeTile(g, new RectangleF(r.X + 18, r.Y + 44, w, 96), "Turbo",
            "En yüksek performans. Oyun ve ağır işler için.\nWindows güç planı: Yüksek performans",
            fan.Perf == PerfMode.Turbo, Theme.Accent, canSwitch, PerfMode.Turbo);
        DrawModeTile(g, new RectangleF(r.X + 18 + w + 12, r.Y + 44, w, 96), "Dengeli",
            "Günlük kullanım için. Daha serin ve sessiz.\nWindows güç planı: Dengeli",
            fan.Perf == PerfMode.Dengeli, Theme.Cpu, canSwitch, PerfMode.Dengeli);

        if (!canSwitch)
        {
            using var font = Theme.Font(12);
            using var muted = new SolidBrush(Theme.Muted);
            using var far = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString("Bu cihazda performans modu değiştirilemiyor", font, muted,
                new RectangleF(r.X, r.Y + 14, r.Width - 18, 18), far);
        }
    }

    private void DrawModeTile(Graphics g, RectangleF rect, string title, string description,
        bool active, Color color, bool enabled, PerfMode mode)
    {
        Color fill = active ? Color.FromArgb(40, color) : enabled && Hot(rect) ? Color.FromArgb(40, 40, 48) : Color.FromArgb(33, 33, 39);
        Theme.FillRounded(g, fill, rect, 10);
        using (var path = Theme.RoundedRect(rect, 10))
        using (var pen = new Pen(active ? color : Theme.CardBorder, active ? 2 : 1))
            g.DrawPath(pen, path);

        using var titleFont = Theme.Font(18, true);
        using var font = Theme.Font(12);
        using var text = new SolidBrush(enabled ? Theme.Text : Theme.Muted);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(title, titleFont, text, rect.X + 16, rect.Y + 12);
        g.DrawString(description, font, muted, new RectangleF(rect.X + 16, rect.Y + 44, rect.Width - 32, 44));

        if (active)
        {
            using var badge = Theme.Font(11, true);
            using var cb = new SolidBrush(color);
            using var far = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString("Etkin", badge, cb, new RectangleF(rect.X, rect.Y + 17, rect.Width - 16, 16), far);
        }
        if (enabled) Hit(rect, () => PerfSelected?.Invoke(mode));
    }

    // ---------------- Ekran kartı hız aşırtma ----------------

    private void DrawOcCard(Graphics g, RectangleF r)
    {
        DrawCard(g, r);
        DrawLabel(g, "Ekran kartı hız aşırtma", r.X + 18, r.Y + 16);
        using var font = Theme.Font(12);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);

        if (!Oc.Available)
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var mid = Theme.Font(14);
            g.DrawString(Oc.Message, mid, muted, new RectangleF(r.X, r.Y + 40, r.Width, r.Height - 60), fmt);
            return;
        }

        // Aç/kapat anahtarı
        var sw = new RectangleF(r.Right - 18 - 40, r.Y + 13, 40, 22);
        Theme.FillRounded(g, OcEnabled ? Theme.Accent : Theme.Track, sw, 11);
        g.FillEllipse(Brushes.White, OcEnabled ? sw.Right - 20 : sw.X + 2, sw.Y + 2, 18, 18);
        Hit(sw, () => OcRequested?.Invoke(!OcEnabled, CoreTarget, MemoryTarget));
        using (var far = new StringFormat { Alignment = StringAlignment.Far })
            g.DrawString(OcEnabled ? "Açık" : "Kapalı", font, OcEnabled ? text : muted,
                new RectangleF(sw.X - 110, r.Y + 16, 100, 18), far);

        // Ayar satırları (sol)
        float left = r.X + 18, rowW = 470;
        DrawClockRow(g, left, r.Y + 56, rowW, "Çekirdek saati", CoreTarget, CoreMax, CoreStep, Oc.Core.OffsetMhz, Theme.Gpu,
            v => CoreTarget = v);
        DrawClockRow(g, left, r.Y + 132, rowW, "Bellek saati", MemoryTarget, MemoryMax, MemoryStep, Oc.Memory.OffsetMhz, Theme.Cpu,
            v => MemoryTarget = v);

        // Hazır ayarlar + uygula
        DrawLabel(g, "Hazır ayarlar", left, r.Y + 216);
        for (int i = 0; i < Presets.Length; i++)
        {
            var p = Presets[i];
            int core = Math.Min(p.core, CoreMax), mem = Math.Min(p.memory, MemoryMax);
            Button(g, new RectangleF(left + 122 + i * 88, r.Y + 206, 82, 34), p.name,
                CoreTarget == core && MemoryTarget == mem, () => { CoreTarget = core; MemoryTarget = mem; },
                activeColor: Color.FromArgb(70, 70, 82));
        }
        bool pending = OcEnabled && (CoreTarget != Oc.Core.OffsetMhz || MemoryTarget != Oc.Memory.OffsetMhz);
        Button(g, new RectangleF(left + rowW - 78, r.Y + 206, 78, 34), "Uygula", pending,
            () => OcRequested?.Invoke(true, CoreTarget, MemoryTarget), enabled: pending);

        // Açıklama (sağ)
        float ix = left + rowW + 28, iw = r.Right - 18 - ix;
        using var sep = new Pen(Theme.CardBorder);
        g.DrawLine(sep, ix - 14, r.Y + 52, ix - 14, r.Bottom - 18);
        using var bold = Theme.Font(12, true);
        g.DrawString("Ne işe yarar?", bold, text, ix, r.Y + 54);
        g.DrawString("Ekran kartını fabrika hızının biraz üstünde çalıştırır; oyunlarda genellikle birkaç FPS kazandırır. " +
                     "Kart daha fazla ısınır.",
            font, muted, new RectangleF(ix, r.Y + 74, iw, 70));
        g.DrawString("Sorun olursa", bold, text, ix, r.Y + 150);
        g.DrawString("Görüntüde bozulma, donma ya da oyun çökmesi görürsen değerleri düşür ya da kapat. " +
                     "Ayar kalıcı değildir; program kapanınca sıfırlanır.",
            font, muted, new RectangleF(ix, r.Y + 170, iw, 90));
    }

    private int CoreMax => Math.Max(0, Math.Min(CoreLimit, Oc.Core.Editable ? Oc.Core.MaxMhz : 0));
    private int MemoryMax => Math.Max(0, Math.Min(MemoryLimit, Oc.Memory.Editable ? Oc.Memory.MaxMhz : 0));

    private void DrawClockRow(Graphics g, float x, float y, float w, string label, int target, int max, int step,
        int applied, Color color, Action<int> set)
    {
        using var labelFont = Theme.Font(11, true);
        using var valueFont = Theme.Number(22);
        using var small = Theme.Font(11);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(max > 0 ? Theme.Text : Theme.Muted);

        g.DrawString(label, labelFont, muted, x, y);
        g.DrawString(applied != 0 ? $"şu an uygulanan: +{applied} MHz" : "şu an uygulanan: yok", small, muted, x + 130, y);

        Button(g, new RectangleF(x, y + 22, 40, 36), "−", false, () => set(Math.Max(0, target - step)), enabled: target > 0, fontSize: 18);
        Button(g, new RectangleF(x + w - 40, y + 22, 40, 36), "+", false, () => set(Math.Min(max, target + step)), enabled: target < max, fontSize: 18);

        g.DrawString($"+{target} MHz", valueFont, text, x + 52, y + 26);
        var bar = new RectangleF(x + 190, y + 36, w - 190 - 52, 8);
        Theme.Bar(g, bar, max > 0 ? target * 100f / max : 0, color);
        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"en çok +{max}", small, muted, new RectangleF(bar.X, y + 48, bar.Width, 14), far);
    }
}
