using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SicaklikPaneli;

/// <summary>Ana pencere. Kapatıldığında program kapanmaz, yalnızca pencere gizlenir.</summary>
public sealed class DashboardForm : Form
{
    public DashboardView View { get; }
    public FanView Fans { get; }
    public PerfView Perf { get; }
    private readonly PanelView[] _pages;

    public DashboardForm()
    {
        Text = "Sıcaklık Paneli";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        AutoScaleMode = AutoScaleMode.None;
        Icon = CreateAppIcon(32);

        View = new DashboardView { Dock = DockStyle.Fill };
        Fans = new FanView { Dock = DockStyle.Fill, Visible = false };
        Perf = new PerfView { Dock = DockStyle.Fill, Visible = false };
        _pages = new PanelView[] { View, Fans, Perf };
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].ActiveTab = i;
            _pages[i].TabSelected += ShowPage;
            Controls.Add(_pages[i]);
        }
        ApplyScale();
    }

    public bool IsPageVisible(PanelView page) => Visible && page.Visible;

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
        _pages[index].Invalidate();
    }

    public void SetRtss(bool connected, bool installed)
    {
        foreach (var p in _pages) { p.RtssConnected = connected; p.RtssInstalled = installed; }
    }

    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        ClientSize = new Size((int)(PanelView.LogicalWidth * scale), (int)(PanelView.LogicalHeight * scale));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Koyu başlık çubuğu (Windows 10 1903+)
        int on = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Pencere simgesi: koyu zemin üzerinde kırmızı gösterge yayı.</summary>
    public static Icon CreateAppIcon(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRounded(g, Color.FromArgb(27, 27, 32), new RectangleF(0, 0, size, size), size * 0.22f);
            float pad = size * 0.2f, th = size * 0.12f;
            var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            using var track = new Pen(Theme.Track, th) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var arc = new Pen(Theme.Accent, th) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(track, rect, 135, 270);
            g.DrawArc(arc, rect, 135, 190);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
