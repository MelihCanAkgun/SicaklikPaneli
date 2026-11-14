using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>"Fanlar" sayfası: fan durumu, mod seçimi, sürüklenebilir fan eğrisi ve sıcaklık koruması.</summary>
public sealed class FanView : PanelView
{
    private const float AxisTempMin = 30, AxisTempMax = 100, AxisSpeedMax = 150;
    private static readonly RectangleF CurveCard = new(20, 176, 560, 336);
    private static readonly RectangleF Plot = new(CurveCard.X + 52, CurveCard.Y + 60, CurveCard.Width - 52 - 26, CurveCard.Height - 60 - 44);

    public Snapshot Data = Snapshot.Empty;
    public bool ProtectionEnabled;
    public int ProtectionTemp = 90;

    public event Action<FanCurve, FanCurve>? ApplyRequested;
    public event Action? AutoRequested;
    public event Action<bool>? BoostRequested;
    public event Action<int, int>? FixedRequested; // işlemci, ekran kartı (RPM)
    public event Action<bool, int>? ProtectionChanged;

    /// <summary>Sabit devir hedefleri (RPM). Çubuk sürüklenirken yalnızca burada değişir, bırakınca uygulanır.</summary>
    public int FixedCpuTarget = 3000, FixedGpuTarget = 3000;
    /// <summary>Gönderilen son komutun sıra numarası; EC bunu işleyene kadar seçim "bekliyor" sayılır.</summary>
    public int PendingSeq;

    private enum Choice { Yok, Otomatik, Ozel, Sabit, TamHiz }
    // Tıklanan mod, EC onaylayana kadar hemen seçili gösterilir
    private Choice _pending = Choice.Yok;
    private DateTime _pendingAt;
    private bool _forceCurve;
    private int _sliderDrag = -1, _sliderHover = -1; // 0: işlemci, 1: ekran kartı

    // Sabit devir kartındaki iki blok (işlemci, ekran kartı)
    private const float FixedBlockTop = 48, FixedBlockHeight = 124;
    private static float BlockY(int fan) => CurveCard.Y + FixedBlockTop + fan * FixedBlockHeight;
    private static RectangleF SliderTrack(int fan) => new(CurveCard.X + 34, BlockY(fan) + 78, CurveCard.Width - 68, 6);

    // Düzenlenen (henüz uygulanmamış olabilir) eğriler
    private FanCurve? _cpu, _gpu;
    private int _loadedRevision = -1;
    private bool _dirty, _editGpu;
    private int _drag = -1, _hoverHandle = -1;
    private string _localMessage = "";

    private static readonly (string name, FanCurve cpu, FanCurve gpu)[] Presets =
    {
        ("Sessiz",
            new FanCurve(new[] { 60, 68, 75, 82, 88, 93 }, new[] { 30, 40, 50, 65, 85, 110, 150 }),
            new FanCurve(new[] { 58, 65, 71, 77, 83, 88 }, new[] { 0, 30, 45, 60, 80, 110, 150 })),
        ("Dengeli",
            new FanCurve(new[] { 55, 64, 71, 78, 85, 90 }, new[] { 40, 50, 60, 80, 100, 125, 150 }),
            new FanCurve(new[] { 55, 61, 67, 73, 79, 85 }, new[] { 0, 35, 50, 70, 100, 125, 150 })),
        ("Performans",
            new FanCurve(new[] { 50, 58, 65, 72, 80, 86 }, new[] { 50, 65, 80, 100, 125, 150, 150 }),
            new FanCurve(new[] { 50, 56, 62, 68, 75, 82 }, new[] { 40, 55, 70, 90, 120, 150, 150 })),
    };

    /// <summary>Yeni ölçüm geldiğinde çağrılır. Kullanıcı düzenleme yapmıyorsa EC'deki eğriyi yükler.</summary>
    public void SetData(Snapshot s)
    {
        Data = s;
        var fan = s.Fan;
        if (_pending != Choice.Yok && (fan.CommandSeq >= PendingSeq || DateTime.UtcNow - _pendingAt > TimeSpan.FromSeconds(6)))
            _pending = Choice.Yok;
        if (_pending == Choice.Yok && _sliderDrag < 0)
        {
            if (fan.FixedCpuRpm is int c) FixedCpuTarget = c;
            if (fan.FixedGpuRpm is int g) FixedGpuTarget = g;
        }
        if (fan.Available && fan.Cpu != null && fan.Gpu != null &&
            (_cpu == null || (fan.Revision != _loadedRevision && !_dirty)))
        {
            _cpu = fan.Cpu.Copy();
            _gpu = fan.Gpu.Copy();
            _loadedRevision = fan.Revision;
        }
    }

    private FanCurve? Editing => _editGpu ? _gpu : _cpu;

    /// <summary>Tam hız dışındaki temel mod (tam hız kapatılınca dönülecek olan).</summary>
    private static Choice Underlying(FanState fan) => fan.Selected switch
    {
        FanSelect.Otomatik => Choice.Otomatik,
        FanSelect.Ozel => Choice.Ozel,
        FanSelect.Sabit => Choice.Sabit,
        _ => Choice.Yok
    };

    /// <summary>Ekranda seçili görünen mod: bekleyen tıklama varsa o, yoksa programın seçtiği mod.</summary>
    private Choice Current(FanState fan) =>
        _pending != Choice.Yok ? _pending
      : fan.ManualBoost ? Choice.TamHiz
      : Underlying(fan);

    private bool FixedView => !_forceCurve && Current(Data.Fan) == Choice.Sabit;

    private void SetPending(Choice c)
    {
        _pending = c;
        _pendingAt = DateTime.UtcNow;
        _forceCurve = false;
        _localMessage = "";
    }

    private void RequestFixed()
    {
        SetPending(Choice.Sabit);
        FixedRequested?.Invoke(FixedCpuTarget, FixedGpuTarget);
    }

    protected override void DrawContent(Graphics g)
    {
        var fan = Data.Fan;
        if (!fan.Available || _cpu == null || _gpu == null)
        {
            DrawUnavailable(g, fan.Message);
            return;
        }

        DrawFanCard(g, new RectangleF(20, 68, 200, 96), "İşlemci fanı", Theme.Cpu, fan.CpuRpm, fan.CpuPercent, Data.CpuTemp);
        DrawFanCard(g, new RectangleF(232, 68, 200, 96), "Ekran kartı fanı", Theme.Gpu, fan.GpuRpm, fan.GpuPercent, Data.GpuTemp);
        DrawModeCard(g, new RectangleF(444, 68, 396, 96), fan);
        if (FixedView) DrawFixedCard(g, fan);
        else DrawCurveCard(g, fan);
        DrawSideCard(g, new RectangleF(592, 176, 248, 336), fan);
        DrawFooter(g, fan);
    }

    private static void DrawUnavailable(Graphics g, string message)
    {
        var r = new RectangleF(20, 68, 820, 512);
        DrawCard(g, r);
        using var big = Theme.Font(18, true);
        using var font = Theme.Font(13);
        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("Fan denetimi kullanılamıyor", big, text, new RectangleF(r.X, r.Y + 190, r.Width, 30), fmt);
        g.DrawString(message, font, muted, new RectangleF(r.X + 80, r.Y + 228, r.Width - 160, 60), fmt);
    }

    // ---------------- Üst satır ----------------

    private static void DrawFanCard(Graphics g, RectangleF r, string label, Color color, int rpm, int percent, float? temp)
    {
        DrawCard(g, r);
        DrawLabel(g, label, r.X + 16, r.Y + 12, color);
        using var big = Theme.Number(32);
        using var small = Theme.Font(12);
        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        string value = rpm > 0 ? rpm.ToString() : "0";
        g.DrawString(value, big, text, r.X + 12, r.Y + 32);
        float w = g.MeasureString(value, big).Width;
        g.DrawString("RPM", small, muted, r.X + 8 + w, r.Y + 32 + Theme.Ascent(big) - Theme.Ascent(small));
        string sub = $"%{percent}" + (temp.HasValue ? $"  ·  {temp.Value:0}°C" : "");
        g.DrawString(rpm > 0 ? sub : "Duruyor" + (temp.HasValue ? $"  ·  {temp.Value:0}°C" : ""), small, muted, r.X + 16, r.Y + 72);
    }

    private void DrawModeCard(Graphics g, RectangleF r, FanState fan)
    {
        DrawCard(g, r);
        DrawLabel(g, "Fan modu", r.X + 16, r.Y + 12);

        var current = Current(fan);
        bool full = current == Choice.TamHiz;

        float y = r.Y + 42, gap = 8, w = (r.Width - 32 - 3 * gap) / 4;
        RectangleF Slot(int i) => new(r.X + 16 + i * (w + gap), y, w, 38);
        Button(g, Slot(0), "Otomatik", current == Choice.Otomatik, () =>
        {
            SetPending(Choice.Otomatik);
            AutoRequested?.Invoke();
        });
        Button(g, Slot(1), "Özel eğri", current == Choice.Ozel, () =>
        {
            // Düzenleyicide her zaman son kaydedilen özel eğri durur (sabit devirde EC'deki düz eğri değil)
            _forceCurve = true;
            Apply();
        });
        Button(g, Slot(2), "Sabit devir", current == Choice.Sabit, RequestFixed);
        Button(g, Slot(3), "Tam hız", full, () =>
        {
            // Kapatınca alttaki moda dönülür
            SetPending(full ? Underlying(fan) : Choice.TamHiz);
            BoostRequested?.Invoke(!full);
        });

        if (fan.AutoBoostActive)
        {
            using var font = Theme.Font(11, true);
            using var b = new SolidBrush(Color.FromArgb(255, 150, 150));
            using var far = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString("Sıcaklık koruması etkin", font, b, new RectangleF(r.X, r.Y + 13, r.Width - 16, 16), far);
        }
    }

    // ---------------- Eğri düzenleyici ----------------

    private static float X(float temp) => Plot.X + (temp - AxisTempMin) / (AxisTempMax - AxisTempMin) * Plot.Width;
    private static float Y(float speed) => Plot.Bottom - speed / AxisSpeedMax * Plot.Height;

    private static PointF HandlePoint(FanCurve c, int k) =>
        new(X(k == 0 ? AxisTempMin : c.Temps[k - 1]), Y(c.Speeds[k]));

    private void DrawCurveCard(Graphics g, FanState fan)
    {
        var r = CurveCard;
        DrawCard(g, r);
        DrawLabel(g, "Fan eğrisi", r.X + 18, r.Y + 16);
        Button(g, new RectangleF(r.Right - 18 - 232, r.Y + 10, 112, 28), "İşlemci fanı", !_editGpu,
            () => _editGpu = false, activeColor: Color.FromArgb(52, 96, 150), fontSize: 12);
        Button(g, new RectangleF(r.Right - 18 - 116, r.Y + 10, 116, 28), "Ekran kartı fanı", _editGpu,
            () => _editGpu = true, activeColor: Color.FromArgb(58, 120, 62), fontSize: 12);

        var curve = Editing!;
        Color color = _editGpu ? Theme.Gpu : Theme.Cpu;
        using var small = Theme.Font(11);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        using var grid = new Pen(Color.FromArgb(40, 40, 48), 1);

        // Izgara ve eksenler
        using var rightFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        foreach (int s in new[] { 0, 50, 100, 150 })
        {
            g.DrawLine(grid, Plot.X, Y(s), Plot.Right, Y(s));
            g.DrawString($"%{s}", small, muted, new RectangleF(r.X + 4, Y(s) - 8, 44, 16), rightFmt);
        }
        using var centerFmt = new StringFormat { Alignment = StringAlignment.Center };
        for (int t = 30; t <= 100; t += 10)
        {
            g.DrawLine(grid, X(t), Plot.Y, X(t), Plot.Bottom);
            g.DrawString($"{t}°", small, muted, new RectangleF(X(t) - 20, Plot.Bottom + 6, 40, 16), centerFmt);
        }

        // Basamaklı eğri
        var pts = new List<PointF> { new(Plot.X, Y(curve.Speeds[0])) };
        for (int i = 0; i < 6; i++)
        {
            pts.Add(new PointF(X(curve.Temps[i]), Y(curve.Speeds[i])));
            pts.Add(new PointF(X(curve.Temps[i]), Y(curve.Speeds[i + 1])));
        }
        pts.Add(new PointF(Plot.Right, Y(curve.Speeds[6])));

        using (var area = new GraphicsPath())
        {
            var poly = new List<PointF>(pts) { new(Plot.Right, Plot.Bottom), new(Plot.X, Plot.Bottom) };
            area.AddPolygon(poly.ToArray());
            using var fill = new SolidBrush(Color.FromArgb(34, color));
            g.FillPath(fill, area);
        }
        using (var pen = new Pen(color, 2.2f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts.ToArray());

        // Şu anki sıcaklık ve gerçek fan hızı
        float? liveTemp = _editGpu ? Data.GpuTemp : Data.CpuTemp;
        int livePercent = _editGpu ? fan.GpuPercent : fan.CpuPercent;
        if (liveTemp is float lt)
        {
            float lx = X(Math.Clamp(lt, AxisTempMin, AxisTempMax));
            using var dash = new Pen(Color.FromArgb(150, Theme.Text), 1) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, lx, Plot.Y, lx, Plot.Bottom);
            float ly = Y(Math.Clamp(livePercent, 0, AxisSpeedMax));
            using var wb = new SolidBrush(Theme.Text);
            g.FillEllipse(wb, lx - 4, ly - 4, 8, 8);
            bool leftSide = lx > Plot.Right - 70;
            using var fmt = new StringFormat { Alignment = leftSide ? StringAlignment.Far : StringAlignment.Near };
            g.DrawString($"şu an {lt:0}°", small, text,
                new RectangleF(leftSide ? lx - 86 : lx + 6, Plot.Y - 2, 80, 16), fmt);
        }

        // Tutamaklar
        using var labelFont = Theme.Font(11, true);
        for (int k = 0; k < 7; k++)
        {
            var p = HandlePoint(curve, k);
            bool hot = k == _drag || (k == _hoverHandle && _drag < 0);
            float rad = hot ? 7.5f : 5.5f;
            using var hb = new SolidBrush(hot ? Color.White : color);
            using var border = new Pen(Theme.Card, 2);
            g.FillEllipse(hb, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            g.DrawEllipse(border, p.X - rad, p.Y - rad, rad * 2, rad * 2);

            string label = k == 0 || !hot ? $"{curve.Speeds[k]}" : $"{curve.Temps[k - 1]}°C · %{curve.Speeds[k]}";
            float lw = g.MeasureString(label, labelFont).Width;
            float tx = Math.Clamp(p.X - lw / 2 + (k == 0 ? 10 : 0), Plot.X, Plot.Right - lw);
            float ty = p.Y - 24 < Plot.Y - 14 ? p.Y + 10 : p.Y - 24;
            if (hot) Theme.FillRounded(g, Color.FromArgb(230, 12, 12, 15), new RectangleF(tx - 4, ty - 2, lw + 8, 19), 5);
            g.DrawString(label, labelFont, hot ? text : muted, tx, ty);
        }
    }

    // ---------------- Sabit devir ----------------

    private static float SliderX(int fan, int rpm)
    {
        var t = SliderTrack(fan);
        return t.X + (rpm - FanController.FixedMinRpm) / (float)(FanController.FixedMaxRpm - FanController.FixedMinRpm) * t.Width;
    }

    private static int SliderValue(int fan, float x)
    {
        var t = SliderTrack(fan);
        float k = Math.Clamp((x - t.X) / t.Width, 0, 1);
        int rpm = FanController.FixedMinRpm + (int)Math.Round(k * (FanController.FixedMaxRpm - FanController.FixedMinRpm) / 100f) * 100;
        return Math.Clamp(rpm, FanController.FixedMinRpm, FanController.FixedMaxRpm);
    }

    /// <summary>Verilen noktadaki çubuk (0: işlemci, 1: ekran kartı, -1: yok).</summary>
    private int SliderAt(PointF p)
    {
        if (!Data.Fan.Available || !FixedView) return -1;
        for (int i = 0; i < 2; i++)
            if (RectangleF.Inflate(SliderTrack(i), 14, 14).Contains(p)) return i;
        return -1;
    }

    private ref int Target(int fan) => ref fan == 0 ? ref FixedCpuTarget : ref FixedGpuTarget;

    private void DrawFixedCard(Graphics g, FanState fan)
    {
        var r = CurveCard;
        DrawCard(g, r);
        DrawLabel(g, "Sabit devir", r.X + 18, r.Y + 16);

        using var small = Theme.Font(11);
        using var font = Theme.Font(12);
        using var muted = new SolidBrush(Theme.Muted);
        using var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("Her fan kendi hedef devrinde tutulur", font, muted, new RectangleF(r.X, r.Y + 16, r.Width - 18, 18), far);

        DrawFixedBlock(g, 0, "İşlemci fanı", Theme.Cpu, fan.CpuRpm, fan.CpuPercent, Data.CpuTemp, fan);
        using (var sep = new Pen(Theme.CardBorder))
            g.DrawLine(sep, r.X + 18, BlockY(1) - 10, r.Right - 18, BlockY(1) - 10);
        DrawFixedBlock(g, 1, "Ekran kartı fanı", Theme.Gpu, fan.GpuRpm, fan.GpuPercent, Data.GpuTemp, fan);

        g.DrawString($"Güvenlik: {FanController.FixedSafetyTemp}°C üstünde fanlar her durumda tam hıza çıkar.",
            small, muted, new RectangleF(r.X + 18, r.Bottom - 28, r.Width - 36, 18));
    }

    private void DrawFixedBlock(Graphics g, int index, string label, Color color, int rpm, int percent, float? temp, FanState fan)
    {
        var r = CurveCard;
        float y = BlockY(index);
        int target = Target(index);
        int? applied = index == 0 ? fan.FixedCpuRpm : fan.FixedGpuRpm;

        using var font = Theme.Font(12);
        using var small = Theme.Font(11);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        using var far = new StringFormat { Alignment = StringAlignment.Far };

        // Başlık ve anlık durum
        DrawLabel(g, label, r.X + 18, y, color);
        (string status, Color c) = applied == null || _pending == Choice.Sabit || applied != target ? ("Uygulanıyor…", Theme.Muted)
            : fan.Boost ? ("Tam hız açık", Color.FromArgb(255, 150, 150))
            : temp >= FanController.FixedSafetyTemp ? ("Sıcak: güvenlik için tam hız", Color.FromArgb(255, 150, 150))
            : Math.Abs(rpm - target) <= target * 0.06 ? ("Hedefte", Theme.Ok)
            : percent >= FanCurve.MaxSpeed && rpm < target ? ("Bu devre ulaşamıyor", Color.FromArgb(240, 220, 90))
            : percent <= FanController.FixedMinSpeed && rpm > target ? ("Daha yavaş dönemiyor", Color.FromArgb(240, 220, 90))
            : ("Ayarlanıyor…", Theme.Muted);
        using var sb = new SolidBrush(c);
        float sw = g.MeasureString(status, font).Width;
        g.DrawString(status, font, sb, new RectangleF(r.X, y, r.Width - 18, 18), far);
        g.DrawString($"şu an {rpm} RPM  ·", font, muted, new RectangleF(r.X, y, r.Width - 18 - sw, 18), far);

        // Hedef değer ve ince ayar düğmeleri
        using var big = Theme.Number(30);
        string value = target.ToString();
        g.DrawString(value, big, text, r.X + 14, y + 22);
        float vw = g.MeasureString(value, big).Width;
        g.DrawString("RPM hedef", font, muted, r.X + 10 + vw, y + 22 + Theme.Ascent(big) - Theme.Ascent(font));
        Button(g, new RectangleF(r.Right - 18 - 36 - 8 - 36, y + 24, 36, 34), "−", false, () => StepFixed(index, -100), fontSize: 18);
        Button(g, new RectangleF(r.Right - 18 - 36, y + 24, 36, 34), "+", false, () => StepFixed(index, +100), fontSize: 18);

        // Çubuk
        var track = SliderTrack(index);
        float kx = SliderX(index, target);
        Theme.FillRounded(g, Theme.Track, track, 3);
        Theme.FillRounded(g, color, new RectangleF(track.X, track.Y, kx - track.X, track.Height), 3);
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        using var tick = new Pen(Theme.CardBorder, 1);
        for (int v = 2000; v <= FanController.FixedMaxRpm; v += 500)
        {
            float tx = SliderX(index, v);
            g.DrawLine(tick, tx, track.Bottom + 6, tx, track.Bottom + 10);
            if (v % 1000 == 0)
                g.DrawString(v.ToString(), small, muted, new RectangleF(tx - 24, track.Bottom + 11, 48, 16), center);
        }
        float kr = _sliderDrag == index || _sliderHover == index ? 11 : 9;
        float ky = track.Y + track.Height / 2;
        g.FillEllipse(Brushes.White, kx - kr, ky - kr, kr * 2, kr * 2);
        using var ring = new Pen(color, 2);
        g.DrawEllipse(ring, kx - kr, ky - kr, kr * 2, kr * 2);
    }

    private void StepFixed(int fan, int delta)
    {
        ref int target = ref Target(fan);
        target = Math.Clamp(target + delta, FanController.FixedMinRpm, FanController.FixedMaxRpm);
        RequestFixed();
    }

    // ---------------- Sağ sütun: koruma ve hazır ayarlar ----------------

    private void DrawSideCard(Graphics g, RectangleF r, FanState fan)
    {
        DrawCard(g, r);
        DrawLabel(g, "Sıcaklık koruması", r.X + 16, r.Y + 16);

        // Aç/kapat anahtarı
        var sw = new RectangleF(r.Right - 16 - 40, r.Y + 13, 40, 22);
        Theme.FillRounded(g, ProtectionEnabled ? Theme.Accent : Theme.Track, sw, 11);
        float knobX = ProtectionEnabled ? sw.Right - 20 : sw.X + 2;
        g.FillEllipse(Brushes.White, knobX, sw.Y + 2, 18, 18);
        Hit(sw, () => { ProtectionEnabled = !ProtectionEnabled; ProtectionChanged?.Invoke(ProtectionEnabled, ProtectionTemp); });

        using var font = Theme.Font(12);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString("İşlemci ya da ekran kartı bu sıcaklığa ulaşırsa fanlar tam hıza çıkar, soğuyunca eski haline döner.",
            font, muted, new RectangleF(r.X + 16, r.Y + 44, r.Width - 32, 54));

        // Eşik ayarı
        float sy = r.Y + 106;
        Button(g, new RectangleF(r.X + 16, sy, 44, 38), "−", false, () => ChangeProtectionTemp(-1), fontSize: 18);
        Button(g, new RectangleF(r.Right - 16 - 44, sy, 44, 38), "+", false, () => ChangeProtectionTemp(+1), fontSize: 18);
        using var big = Theme.Number(26);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var valueBrush = new SolidBrush(ProtectionEnabled ? Theme.Text : Theme.Muted);
        g.DrawString($"{ProtectionTemp}°C", big, valueBrush, new RectangleF(r.X + 60, sy, r.Width - 120, 38), fmt);

        using var sep = new Pen(Theme.CardBorder);
        g.DrawLine(sep, r.X + 16, r.Y + 166, r.Right - 16, r.Y + 166);

        // Hazır ayarlar
        DrawLabel(g, "Hazır ayarlar", r.X + 16, r.Y + 182);
        float bw = (r.Width - 32 - 8) / 2, by = r.Y + 208;
        for (int i = 0; i < Presets.Length; i++)
        {
            var preset = Presets[i];
            var rect = new RectangleF(r.X + 16 + (i % 2) * (bw + 8), by + (i / 2) * 44, bw, 36);
            bool selected = _cpu!.SameAs(preset.cpu) && _gpu!.SameAs(preset.gpu);
            Button(g, rect, preset.name, selected, () => LoadCurves(preset.cpu, preset.gpu), activeColor: Color.FromArgb(70, 70, 82));
        }
        bool factory = _cpu!.SameAs(fan.FactoryCpu) && _gpu!.SameAs(fan.FactoryGpu);
        Button(g, new RectangleF(r.X + 16 + bw + 8, by + 44, bw, 36), "Fabrika", factory,
            () => { if (fan.FactoryCpu != null && fan.FactoryGpu != null) LoadCurves(fan.FactoryCpu, fan.FactoryGpu); },
            activeColor: Color.FromArgb(70, 70, 82));

        g.DrawString("Bir ayar seçtikten sonra “Uygula”ya basın.", font, muted,
            new RectangleF(r.X + 16, by + 92, r.Width - 32, 36));
    }

    private void ChangeProtectionTemp(int delta)
    {
        ProtectionTemp = Math.Clamp(ProtectionTemp + delta, 70, 97);
        ProtectionChanged?.Invoke(ProtectionEnabled, ProtectionTemp);
    }

    private void LoadCurves(FanCurve cpu, FanCurve gpu)
    {
        _cpu = cpu.Copy();
        _gpu = gpu.Copy();
        _forceCurve = true; // sabit devir ekranındayken seçilen eğri görünsün
        UpdateDirty();
    }

    // ---------------- Alt satır ----------------

    private void DrawFooter(Graphics g, FanState fan)
    {
        string message = _dirty ? "Uygulanmamış değişiklikler var." : _localMessage.Length > 0 ? _localMessage : fan.Message;
        using var font = Theme.Font(13);
        using var brush = new SolidBrush(_dirty ? Color.FromArgb(240, 220, 90) : Theme.Muted);
        g.DrawString(message, font, brush, new RectangleF(22, 542, 540, 22));
        using var tiny = Theme.Font(11);
        using var muted = new SolidBrush(Color.FromArgb(100, 100, 110));
        g.DrawString($"EC bellenimi: {fan.Firmware}", tiny, muted, 22, 564);

        Button(g, new RectangleF(592, 532, 100, 40), "Geri al", false, Revert, enabled: _dirty);
        // Sabit devir ekranında eğri görünmediği için "Uygula" yalnızca bekleyen eğri değişikliği varsa çalışır
        Button(g, new RectangleF(700, 532, 140, 40), "Uygula", _dirty, Apply, enabled: _dirty || !FixedView);
    }

    private void Apply()
    {
        if (_cpu == null || _gpu == null) return;
        var fan = Data.Fan;
        string err = "";
        bool ok = (_cpu.SameAs(fan.FactoryCpu) || _cpu.IsValid(out err)) &&
                  (_gpu.SameAs(fan.FactoryGpu) || _gpu.IsValid(out err));
        if (!ok) { _localMessage = err; return; }
        SetPending(Choice.Ozel);
        _localMessage = "Uygulanıyor…";
        _dirty = false;
        _loadedRevision = -1; // EC'den geri okunan eğriyi yükle
        ApplyRequested?.Invoke(_cpu.Copy(), _gpu.Copy());
    }

    private void Revert()
    {
        var fan = Data.Fan;
        if (fan.Cpu == null || fan.Gpu == null) return;
        _cpu = fan.Cpu.Copy();
        _gpu = fan.Gpu.Copy();
        _dirty = false;
        _localMessage = "";
    }

    private void UpdateDirty()
    {
        var fan = Data.Fan;
        _dirty = !(_cpu!.SameAs(fan.Cpu) && _gpu!.SameAs(fan.Gpu));
        _localMessage = "";
    }

    // ---------------- Sürükleme ----------------

    protected override bool HandlesCursor => _drag >= 0 || _hoverHandle >= 0 || _sliderDrag >= 0 || _sliderHover >= 0;

    private int HandleAt(PointF p)
    {
        var curve = Editing;
        if (curve == null || !Data.Fan.Available || FixedView) return -1;
        int best = -1;
        float bestDist = 14 * 14;
        for (int k = 0; k < 7; k++)
        {
            var h = HandlePoint(curve, k);
            float d = (h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y);
            if (d < bestDist) { bestDist = d; best = k; }
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var lp = ToLogical(e.Location);
        int slider = SliderAt(lp);
        if (slider >= 0)
        {
            _sliderDrag = slider;
            Target(slider) = SliderValue(slider, lp.X);
            Capture = true;
            Invalidate();
            return;
        }
        _drag = HandleAt(lp);
        if (_drag >= 0)
        {
            Normalize(Editing!);
            UpdateDirty();
            Capture = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = ToLogical(e.Location);
        int hover = _drag >= 0 ? _drag : HandleAt(p);
        if (hover != _hoverHandle) { _hoverHandle = hover; Invalidate(); }
        int sliderHover = _sliderDrag >= 0 ? _sliderDrag : SliderAt(p);
        if (sliderHover != _sliderHover) { _sliderHover = sliderHover; Invalidate(); }
        base.OnMouseMove(e);
        if (HandlesCursor) Cursor = _sliderHover >= 0 ? Cursors.Hand : Cursors.SizeAll;

        if (_sliderDrag >= 0)
        {
            int v = SliderValue(_sliderDrag, p.X);
            if (v != Target(_sliderDrag)) { Target(_sliderDrag) = v; Invalidate(); }
            return;
        }

        if (_drag < 0) return;
        var curve = Editing!;
        int speed = (int)Math.Round((Plot.Bottom - p.Y) / Plot.Height * AxisSpeedMax);
        SetSpeed(curve, _drag, speed);
        if (_drag > 0)
        {
            int temp = (int)Math.Round(AxisTempMin + (p.X - Plot.X) / Plot.Width * (AxisTempMax - AxisTempMin));
            SetTemp(curve, _drag - 1, temp);
        }
        UpdateDirty();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_sliderDrag >= 0)
        {
            _sliderDrag = -1;
            Capture = false;
            RequestFixed(); // bırakınca uygulanır
            Invalidate();
        }
        if (_drag >= 0) { _drag = -1; Capture = false; Invalidate(); }
    }

    /// <summary>Hızı ayarlar; eğri azalmayan kalsın diye komşu kademeleri de iter.</summary>
    private static void SetSpeed(FanCurve c, int k, int value)
    {
        value = Math.Clamp(value, k == 6 ? FanCurve.MinTopSpeed : 0, FanCurve.MaxSpeed);
        c.Speeds[k] = value;
        for (int j = k + 1; j < 7; j++) c.Speeds[j] = Math.Max(c.Speeds[j], value);
        for (int j = k - 1; j >= 0; j--) c.Speeds[j] = Math.Min(c.Speeds[j], value);
    }

    /// <summary>Eşiği ayarlar; sıra bozulmasın diye komşu eşikleri de iter.</summary>
    private static void SetTemp(FanCurve c, int i, int value)
    {
        const int gap = FanCurve.MinGap;
        value = Math.Clamp(value, FanCurve.MinTemp + i * gap, FanCurve.MaxTemp - (5 - i) * gap);
        c.Temps[i] = value;
        for (int j = i + 1; j < 6; j++) c.Temps[j] = Math.Max(c.Temps[j], value + (j - i) * gap);
        for (int j = i - 1; j >= 0; j--) c.Temps[j] = Math.Min(c.Temps[j], value - (i - j) * gap);
    }

    /// <summary>Düzenlemeye başlamadan önce eğriyi güvenlik kurallarına uydurur.</summary>
    private static void Normalize(FanCurve c)
    {
        for (int j = 1; j < 7; j++) c.Speeds[j] = Math.Max(c.Speeds[j], c.Speeds[j - 1]);
        c.Speeds[6] = Math.Max(c.Speeds[6], FanCurve.MinTopSpeed);
        for (int j = 0; j < 6; j++)
            c.Temps[j] = Math.Clamp(c.Temps[j], FanCurve.MinTemp + j * FanCurve.MinGap, FanCurve.MaxTemp - (5 - j) * FanCurve.MinGap);
        for (int j = 1; j < 6; j++) c.Temps[j] = Math.Max(c.Temps[j], c.Temps[j - 1] + FanCurve.MinGap);
    }
}
