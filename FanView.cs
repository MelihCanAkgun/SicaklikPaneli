using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>"Fanlar" sayfası: fan durumu, mod seçimi, sürüklenebilir fan eğrisi ve sıcaklık koruması.</summary>
public sealed class FanView : PanelView
{
    private const float AxisTempMin = 30, AxisTempMax = 100, AxisSpeedMax = 150;
    private static readonly RectangleF CurveCard = new(20, 176, 560, 336);
    private static readonly RectangleF Plot = new(CurveCard.X + 52, CurveCard.Y + 60, CurveCard.Width - 52 - 26, CurveCard.Height - 60 - 44);
    /// <summary>Bekleyen bir komutun EC tarafından işlenmesi için beklenen en uzun süre.</summary>
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(6);

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
    // Uygulanan eğri komutunun sıra numarası: EC işleyene kadar düzenleyiciye eski eğri geri yüklenmez
    private int _applySeq;
    private DateTime _applyAt;

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

    // ---------------- Önbellekteki çizim nesneleri ----------------
    // Her karede yeniden oluşturulup atılmasınlar diye bir kez oluşturulur; yalnızca arayüz iş parçacığında kullanılır.

    private static readonly Font Font11 = Theme.Font(11), Font11Bold = Theme.Font(11, true), Font12 = Theme.Font(12),
        Font13 = Theme.Font(13), Font18Bold = Theme.Font(18, true);
    private static readonly Font Number26 = Theme.Number(26), Number30 = Theme.Number(30), Number32 = Theme.Number(32);
    // Büyük sayının yanındaki küçük birimin taban çizgisi hizası
    private static readonly float RpmUnitOffset = Theme.Ascent(Number32) - Theme.Ascent(Font12);
    private static readonly float TargetUnitOffset = Theme.Ascent(Number30) - Theme.Ascent(Font12);

    private static readonly Color WarnColor = Color.FromArgb(255, 150, 150), YellowColor = Color.FromArgb(240, 220, 90);
    private static readonly SolidBrush TextBrush = new(Theme.Text), MutedBrush = new(Theme.Muted),
        DimBrush = new(Color.FromArgb(100, 100, 110)), WarnBrush = new(WarnColor), YellowBrush = new(YellowColor),
        OkBrush = new(Theme.Ok), CpuBrush = new(Theme.Cpu), GpuBrush = new(Theme.Gpu),
        CpuAreaBrush = new(Color.FromArgb(34, Theme.Cpu)), GpuAreaBrush = new(Color.FromArgb(34, Theme.Gpu));
    private static readonly Pen GridPen = new(Color.FromArgb(40, 40, 48), 1), HandleBorderPen = new(Theme.Card, 2),
        SeparatorPen = new(Theme.CardBorder, 1),
        LiveDashPen = new(Color.FromArgb(150, Theme.Text), 1) { DashStyle = DashStyle.Dash },
        CpuCurvePen = new(Theme.Cpu, 2.2f) { LineJoin = LineJoin.Round },
        GpuCurvePen = new(Theme.Gpu, 2.2f) { LineJoin = LineJoin.Round },
        CpuRingPen = new(Theme.Cpu, 2), GpuRingPen = new(Theme.Gpu, 2);
    private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center },
        CenterBothFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center },
        FarFormat = new() { Alignment = StringAlignment.Far },
        FarCenterFormat = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center },
        NearFormat = new() { Alignment = StringAlignment.Near };

    // Sabit metinler (her karede yeniden biçimlendirilmesin)
    private static readonly int[] SpeedGrid = { 0, 50, 100, 150 };
    private static readonly string[] SpeedGridText = Array.ConvertAll(SpeedGrid, s => $"%{s}");
    private static readonly string[] TempAxisText = BuildTempAxis();
    private static readonly string[] SpeedText = BuildSpeedText();
    private static readonly string SafetyText = $"Güvenlik: {FanController.FixedSafetyTemp}°C üstünde fanlar her durumda tam hıza çıkar.";

    private static string[] BuildTempAxis()
    {
        var a = new string[8];
        for (int i = 0; i < a.Length; i++) a[i] = $"{30 + i * 10}°";
        return a;
    }

    private static string[] BuildSpeedText()
    {
        var a = new string[FanCurve.MaxSpeed + 1];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString();
        return a;
    }

    private static string SpeedLabel(int v) => (uint)v < (uint)SpeedText.Length ? SpeedText[v] : v.ToString();

    // Değişince yeniden oluşturulan metinler
    private int _protTextValue = int.MinValue;
    private string _protText = "";
    private string? _firmwareSource;
    private string _firmwareText = "";

    // Eğri çizimi için yeniden kullanılan nokta dizileri (basamaklı çizgi: 14 nokta, alan: +2 köşe)
    private readonly PointF[] _curvePts = new PointF[14];
    private readonly PointF[] _areaPts = new PointF[16];

    // Tıklama işleyicileri bir kez oluşturulur (her çizimde yeni temsilci ayrılmasın)
    private readonly Action _onAuto, _onCurveMode, _onFixedMode, _onBoost, _onEditCpu, _onEditGpu,
        _onProtectionToggle, _onProtectionDown, _onProtectionUp, _onFactory, _onRevert, _onApply;
    private readonly Action[] _onStepDown, _onStepUp, _onPreset;

    public FanView()
    {
        _onAuto = () =>
        {
            SetPending(Choice.Otomatik);
            AutoRequested?.Invoke();
        };
        _onCurveMode = () =>
        {
            // Düzenleyicide her zaman son kaydedilen özel eğri durur (sabit devirde EC'deki düz eğri değil)
            _forceCurve = true;
            Apply();
        };
        _onFixedMode = RequestFixed;
        _onBoost = () =>
        {
            // Kapatınca alttaki moda dönülür
            var fan = Data.Fan;
            bool full = Current(fan) == Choice.TamHiz;
            SetPending(full ? Underlying(fan) : Choice.TamHiz);
            BoostRequested?.Invoke(!full);
        };
        _onEditCpu = () => _editGpu = false;
        _onEditGpu = () => _editGpu = true;
        _onProtectionToggle = () => { ProtectionEnabled = !ProtectionEnabled; ProtectionChanged?.Invoke(ProtectionEnabled, ProtectionTemp); };
        _onProtectionDown = () => ChangeProtectionTemp(-1);
        _onProtectionUp = () => ChangeProtectionTemp(+1);
        _onFactory = () =>
        {
            var fan = Data.Fan;
            if (fan.FactoryCpu != null && fan.FactoryGpu != null) LoadCurves(fan.FactoryCpu, fan.FactoryGpu);
        };
        _onRevert = Revert;
        _onApply = Apply;
        _onStepDown = new Action[] { () => StepFixed(0, -100), () => StepFixed(1, -100) };
        _onStepUp = new Action[] { () => StepFixed(0, +100), () => StepFixed(1, +100) };
        _onPreset = new Action[Presets.Length];
        for (int i = 0; i < Presets.Length; i++)
        {
            var preset = Presets[i];
            _onPreset[i] = () => LoadCurves(preset.cpu, preset.gpu);
        }
    }

    /// <summary>Yeni ölçüm geldiğinde çağrılır. Kullanıcı düzenleme yapmıyorsa EC'deki eğriyi yükler.</summary>
    public void SetData(Snapshot s)
    {
        Data = s;
        var fan = s.Fan;
        var now = DateTime.UtcNow;
        if (_pending != Choice.Yok && (fan.CommandSeq >= PendingSeq || now - _pendingAt > PendingTimeout))
            _pending = Choice.Yok;
        if (_pending == Choice.Yok && _sliderDrag < 0)
        {
            if (fan.FixedCpuRpm is int c) FixedCpuTarget = c;
            if (fan.FixedGpuRpm is int g) FixedGpuTarget = g;
        }
        // Uygulanan eğri henüz işlenmediyse EC'deki eski eğri düzenleyiciye geri yüklenmesin (kısa süreli geri sıçrama);
        // sürükleme sırasında da düzenlenen eğri değiştirilmez
        bool applyInFlight = fan.CommandSeq < _applySeq && now - _applyAt <= PendingTimeout;
        if (fan.Available && fan.Cpu != null && fan.Gpu != null &&
            (_cpu == null || (fan.Revision != _loadedRevision && !_dirty && _drag < 0 && !applyInFlight)))
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
        g.DrawString("Fan denetimi kullanılamıyor", Font18Bold, TextBrush, new RectangleF(r.X, r.Y + 190, r.Width, 30), CenterBothFormat);
        g.DrawString(message, Font13, MutedBrush, new RectangleF(r.X + 80, r.Y + 228, r.Width - 160, 60), CenterBothFormat);
    }

    // ---------------- Üst satır ----------------

    private static void DrawFanCard(Graphics g, RectangleF r, string label, Color color, int rpm, int percent, float? temp)
    {
        DrawCard(g, r);
        DrawLabel(g, label, r.X + 16, r.Y + 12, color);
        string value = rpm > 0 ? rpm.ToString() : "0";
        g.DrawString(value, Number32, TextBrush, r.X + 12, r.Y + 32);
        float w = g.MeasureString(value, Number32).Width;
        g.DrawString("RPM", Font12, MutedBrush, r.X + 8 + w, r.Y + 32 + RpmUnitOffset);
        string sub = rpm > 0
            ? temp.HasValue ? $"%{percent}  ·  {temp.Value:0}°C" : $"%{percent}"
            : temp.HasValue ? $"Duruyor  ·  {temp.Value:0}°C" : "Duruyor";
        g.DrawString(sub, Font12, MutedBrush, r.X + 16, r.Y + 72);
    }

    private void DrawModeCard(Graphics g, RectangleF r, FanState fan)
    {
        DrawCard(g, r);
        DrawLabel(g, "Fan modu", r.X + 16, r.Y + 12);

        var current = Current(fan);

        float y = r.Y + 42, gap = 8, w = (r.Width - 32 - 3 * gap) / 4;
        RectangleF Slot(int i) => new(r.X + 16 + i * (w + gap), y, w, 38);
        Button(g, Slot(0), "Otomatik", current == Choice.Otomatik, _onAuto);
        Button(g, Slot(1), "Özel eğri", current == Choice.Ozel, _onCurveMode);
        Button(g, Slot(2), "Sabit devir", current == Choice.Sabit, _onFixedMode);
        Button(g, Slot(3), "Tam hız", current == Choice.TamHiz, _onBoost);

        if (fan.AutoBoostActive)
            g.DrawString("Sıcaklık koruması etkin", Font11Bold, WarnBrush, new RectangleF(r.X, r.Y + 13, r.Width - 16, 16), FarFormat);
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
            _onEditCpu, activeColor: Color.FromArgb(52, 96, 150), fontSize: 12);
        Button(g, new RectangleF(r.Right - 18 - 116, r.Y + 10, 116, 28), "Ekran kartı fanı", _editGpu,
            _onEditGpu, activeColor: Color.FromArgb(58, 120, 62), fontSize: 12);

        var curve = Editing!;
        var colorBrush = _editGpu ? GpuBrush : CpuBrush;

        // Izgara ve eksenler
        for (int i = 0; i < SpeedGrid.Length; i++)
        {
            float sy = Y(SpeedGrid[i]);
            g.DrawLine(GridPen, Plot.X, sy, Plot.Right, sy);
            g.DrawString(SpeedGridText[i], Font11, MutedBrush, new RectangleF(r.X + 4, sy - 8, 44, 16), FarCenterFormat);
        }
        for (int i = 0, t = 30; t <= 100; i++, t += 10)
        {
            float tx = X(t);
            g.DrawLine(GridPen, tx, Plot.Y, tx, Plot.Bottom);
            g.DrawString(TempAxisText[i], Font11, MutedBrush, new RectangleF(tx - 20, Plot.Bottom + 6, 40, 16), CenterFormat);
        }

        // Basamaklı eğri
        var pts = _curvePts;
        pts[0] = new PointF(Plot.X, Y(curve.Speeds[0]));
        for (int i = 0; i < 6; i++)
        {
            float tx = X(curve.Temps[i]);
            pts[1 + 2 * i] = new PointF(tx, Y(curve.Speeds[i]));
            pts[2 + 2 * i] = new PointF(tx, Y(curve.Speeds[i + 1]));
        }
        pts[13] = new PointF(Plot.Right, Y(curve.Speeds[6]));

        var area = _areaPts;
        Array.Copy(pts, area, pts.Length);
        area[14] = new PointF(Plot.Right, Plot.Bottom);
        area[15] = new PointF(Plot.X, Plot.Bottom);
        g.FillPolygon(_editGpu ? GpuAreaBrush : CpuAreaBrush, area);
        g.DrawLines(_editGpu ? GpuCurvePen : CpuCurvePen, pts);

        // Şu anki sıcaklık ve gerçek fan hızı
        float? liveTemp = _editGpu ? Data.GpuTemp : Data.CpuTemp;
        int livePercent = _editGpu ? fan.GpuPercent : fan.CpuPercent;
        if (liveTemp is float lt)
        {
            float lx = X(Math.Clamp(lt, AxisTempMin, AxisTempMax));
            g.DrawLine(LiveDashPen, lx, Plot.Y, lx, Plot.Bottom);
            float ly = Y(Math.Clamp(livePercent, 0, AxisSpeedMax));
            g.FillEllipse(TextBrush, lx - 4, ly - 4, 8, 8);
            bool leftSide = lx > Plot.Right - 70;
            g.DrawString($"şu an {lt:0}°", Font11, TextBrush,
                new RectangleF(leftSide ? lx - 86 : lx + 6, Plot.Y - 2, 80, 16), leftSide ? FarFormat : NearFormat);
        }

        // Tutamaklar
        for (int k = 0; k < 7; k++)
        {
            var p = HandlePoint(curve, k);
            bool hot = k == _drag || (k == _hoverHandle && _drag < 0);
            float rad = hot ? 7.5f : 5.5f;
            g.FillEllipse(hot ? Brushes.White : colorBrush, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            g.DrawEllipse(HandleBorderPen, p.X - rad, p.Y - rad, rad * 2, rad * 2);

            string label = k == 0 || !hot ? SpeedLabel(curve.Speeds[k]) : $"{curve.Temps[k - 1]}°C · %{curve.Speeds[k]}";
            float lw = g.MeasureString(label, Font11Bold).Width;
            float tx = Math.Clamp(p.X - lw / 2 + (k == 0 ? 10 : 0), Plot.X, Plot.Right - lw);
            float ty = p.Y - 24 < Plot.Y - 14 ? p.Y + 10 : p.Y - 24;
            if (hot) Theme.FillRounded(g, Color.FromArgb(230, 12, 12, 15), new RectangleF(tx - 4, ty - 2, lw + 8, 19), 5);
            g.DrawString(label, Font11Bold, hot ? TextBrush : MutedBrush, tx, ty);
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

        g.DrawString("Her fan kendi hedef devrinde tutulur", Font12, MutedBrush, new RectangleF(r.X, r.Y + 16, r.Width - 18, 18), FarFormat);

        DrawFixedBlock(g, 0, "İşlemci fanı", Theme.Cpu, fan.CpuRpm, fan.CpuPercent, Data.CpuTemp, fan);
        g.DrawLine(SeparatorPen, r.X + 18, BlockY(1) - 10, r.Right - 18, BlockY(1) - 10);
        DrawFixedBlock(g, 1, "Ekran kartı fanı", Theme.Gpu, fan.GpuRpm, fan.GpuPercent, Data.GpuTemp, fan);

        g.DrawString(SafetyText, Font11, MutedBrush, new RectangleF(r.X + 18, r.Bottom - 28, r.Width - 36, 18));
    }

    private void DrawFixedBlock(Graphics g, int index, string label, Color color, int rpm, int percent, float? temp, FanState fan)
    {
        var r = CurveCard;
        float y = BlockY(index);
        int target = Target(index);
        int? applied = index == 0 ? fan.FixedCpuRpm : fan.FixedGpuRpm;

        // Başlık ve anlık durum
        DrawLabel(g, label, r.X + 18, y, color);
        (string status, Brush statusBrush) = applied == null || _pending == Choice.Sabit || applied != target ? ("Uygulanıyor…", MutedBrush)
            : fan.Boost ? ("Tam hız açık", WarnBrush)
            : temp >= FanController.FixedSafetyTemp ? ("Sıcak: güvenlik için tam hız", WarnBrush)
            : Math.Abs(rpm - target) <= target * 0.06 ? ("Hedefte", OkBrush)
            : percent >= FanCurve.MaxSpeed && rpm < target ? ("Bu devre ulaşamıyor", YellowBrush)
            : percent <= FanController.FixedMinSpeed && rpm > target ? ("Daha yavaş dönemiyor", YellowBrush)
            : ("Ayarlanıyor…", MutedBrush);
        float sw = g.MeasureString(status, Font12).Width;
        g.DrawString(status, Font12, statusBrush, new RectangleF(r.X, y, r.Width - 18, 18), FarFormat);
        g.DrawString($"şu an {rpm} RPM  ·", Font12, MutedBrush, new RectangleF(r.X, y, r.Width - 18 - sw, 18), FarFormat);

        // Hedef değer ve ince ayar düğmeleri
        string value = target.ToString();
        g.DrawString(value, Number30, TextBrush, r.X + 14, y + 22);
        float vw = g.MeasureString(value, Number30).Width;
        g.DrawString("RPM hedef", Font12, MutedBrush, r.X + 10 + vw, y + 22 + TargetUnitOffset);
        Button(g, new RectangleF(r.Right - 18 - 36 - 8 - 36, y + 24, 36, 34), "−", false, _onStepDown[index], fontSize: 18);
        Button(g, new RectangleF(r.Right - 18 - 36, y + 24, 36, 34), "+", false, _onStepUp[index], fontSize: 18);

        // Çubuk
        var track = SliderTrack(index);
        float kx = SliderX(index, target);
        Theme.FillRounded(g, Theme.Track, track, 3);
        Theme.FillRounded(g, color, new RectangleF(track.X, track.Y, kx - track.X, track.Height), 3);
        for (int v = 2000; v <= FanController.FixedMaxRpm; v += 500)
        {
            float tx = SliderX(index, v);
            g.DrawLine(SeparatorPen, tx, track.Bottom + 6, tx, track.Bottom + 10);
            if (v % 1000 == 0)
                g.DrawString(RpmTickText(v), Font11, MutedBrush, new RectangleF(tx - 24, track.Bottom + 11, 48, 16), CenterFormat);
        }
        float kr = _sliderDrag == index || _sliderHover == index ? 11 : 9;
        float ky = track.Y + track.Height / 2;
        g.FillEllipse(Brushes.White, kx - kr, ky - kr, kr * 2, kr * 2);
        g.DrawEllipse(index == 0 ? CpuRingPen : GpuRingPen, kx - kr, ky - kr, kr * 2, kr * 2);
    }

    // Çubuk altındaki bin RPM etiketleri (2000…6000)
    private static readonly string[] RpmTicks = { "2000", "3000", "4000", "5000", "6000" };
    private static string RpmTickText(int v) => (v / 1000 - 2) is int i && (uint)i < (uint)RpmTicks.Length ? RpmTicks[i] : v.ToString();

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
        Hit(sw, _onProtectionToggle);

        g.DrawString("İşlemci ya da ekran kartı bu sıcaklığa ulaşırsa fanlar tam hıza çıkar, soğuyunca eski haline döner.",
            Font12, MutedBrush, new RectangleF(r.X + 16, r.Y + 44, r.Width - 32, 54));

        // Eşik ayarı
        float sy = r.Y + 106;
        Button(g, new RectangleF(r.X + 16, sy, 44, 38), "−", false, _onProtectionDown, fontSize: 18);
        Button(g, new RectangleF(r.Right - 16 - 44, sy, 44, 38), "+", false, _onProtectionUp, fontSize: 18);
        if (_protTextValue != ProtectionTemp)
        {
            _protTextValue = ProtectionTemp;
            _protText = $"{ProtectionTemp}°C";
        }
        g.DrawString(_protText, Number26, ProtectionEnabled ? TextBrush : MutedBrush, new RectangleF(r.X + 60, sy, r.Width - 120, 38), CenterBothFormat);

        g.DrawLine(SeparatorPen, r.X + 16, r.Y + 166, r.Right - 16, r.Y + 166);

        // Hazır ayarlar
        DrawLabel(g, "Hazır ayarlar", r.X + 16, r.Y + 182);
        float bw = (r.Width - 32 - 8) / 2, by = r.Y + 208;
        for (int i = 0; i < Presets.Length; i++)
        {
            var preset = Presets[i];
            var rect = new RectangleF(r.X + 16 + (i % 2) * (bw + 8), by + (i / 2) * 44, bw, 36);
            bool selected = _cpu!.SameAs(preset.cpu) && _gpu!.SameAs(preset.gpu);
            Button(g, rect, preset.name, selected, _onPreset[i], activeColor: Color.FromArgb(70, 70, 82));
        }
        bool factory = _cpu!.SameAs(fan.FactoryCpu) && _gpu!.SameAs(fan.FactoryGpu);
        Button(g, new RectangleF(r.X + 16 + bw + 8, by + 44, bw, 36), "Fabrika", factory, _onFactory,
            activeColor: Color.FromArgb(70, 70, 82));

        g.DrawString("Bir ayar seçtikten sonra “Uygula”ya basın.", Font12, MutedBrush,
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
        g.DrawString(message, Font13, _dirty ? YellowBrush : MutedBrush, new RectangleF(22, 542, 540, 22));
        if (!ReferenceEquals(_firmwareSource, fan.Firmware))
        {
            _firmwareSource = fan.Firmware;
            _firmwareText = $"EC bellenimi: {fan.Firmware}";
        }
        g.DrawString(_firmwareText, Font11, DimBrush, 22, 564);

        Button(g, new RectangleF(592, 532, 100, 40), "Geri al", false, _onRevert, enabled: _dirty);
        // Sabit devir ekranında eğri görünmediği için "Uygula" yalnızca bekleyen eğri değişikliği varsa çalışır
        Button(g, new RectangleF(700, 532, 140, 40), "Uygula", _dirty, _onApply, enabled: _dirty || !FixedView);
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
        int before = PendingSeq;
        ApplyRequested?.Invoke(_cpu.Copy(), _gpu.Copy());
        // Olay işleyicisi komutu kuyruğa koyduysa sıra numarası arttı; o işlenene kadar eski eğri yüklenmez
        if (PendingSeq != before)
        {
            _applySeq = PendingSeq;
            _applyAt = DateTime.UtcNow;
        }
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
        int sliderHover = _sliderDrag >= 0 ? _sliderDrag : SliderAt(p);
        if (hover != _hoverHandle || sliderHover != _sliderHover)
        {
            _hoverHandle = hover;
            _sliderHover = sliderHover;
            Invalidate();
        }
        base.OnMouseMove(e);
        if (HandlesCursor) Cursor = _sliderHover >= 0 ? Cursors.Hand : Cursors.SizeAll;

        if (_sliderDrag >= 0)
        {
            int v = SliderValue(_sliderDrag, p.X);
            if (v != Target(_sliderDrag)) { Target(_sliderDrag) = v; Invalidate(); }
            return;
        }

        if (_drag < 0) return;
        var curve = Editing;
        if (curve == null) { _drag = -1; return; }

        // Eğri gerçekten değişmediyse (fare aynı değer içinde kıpırdadı) yeniden çizim ve karşılaştırma yapılmaz
        Span<int> before = stackalloc int[13];
        curve.Temps.AsSpan().CopyTo(before);
        curve.Speeds.AsSpan().CopyTo(before[6..]);

        int speed = (int)Math.Round((Plot.Bottom - p.Y) / Plot.Height * AxisSpeedMax);
        SetSpeed(curve, _drag, speed);
        if (_drag > 0)
        {
            int temp = (int)Math.Round(AxisTempMin + (p.X - Plot.X) / Plot.Width * (AxisTempMax - AxisTempMin));
            SetTemp(curve, _drag - 1, temp);
        }
        if (curve.Temps.AsSpan().SequenceEqual(before[..6]) && curve.Speeds.AsSpan().SequenceEqual(before[6..])) return;
        UpdateDirty();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (EndDrag()) Capture = false;
    }

    /// <summary>Fare yakalaması kaybolursa (pencere değişimi, açılan iletişim kutusu) sürükleme yarıda kalmasın.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndDrag();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // Fare sayfadan çıkınca tutamak ve çubuk vurgusu kalmasın (sürükleme sürerken korunur)
        int hover = _drag >= 0 ? _drag : -1, sliderHover = _sliderDrag >= 0 ? _sliderDrag : -1;
        if (hover != _hoverHandle || sliderHover != _sliderHover)
        {
            _hoverHandle = hover;
            _sliderHover = sliderHover;
            Invalidate();
        }
    }

    /// <summary>Süren sürüklemeyi bitirir; çubuk bırakıldıysa yeni devir uygulanır. Sürükleme varsa true döner.</summary>
    private bool EndDrag()
    {
        bool any = false;
        if (_sliderDrag >= 0)
        {
            _sliderDrag = -1;
            RequestFixed(); // bırakınca uygulanır
            any = true;
        }
        if (_drag >= 0) { _drag = -1; any = true; }
        if (any) Invalidate();
        return any;
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
