namespace SicaklikPaneli;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "SicaklikPaneli_TekKopya", out bool ilkKopya);
        if (!ilkKopya)
        {
            MessageBox.Show("Sıcaklık Paneli zaten çalışıyor (görev çubuğunun sağ alt köşesine bakın).",
                "Sıcaklık Paneli", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
