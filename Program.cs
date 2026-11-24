namespace SicaklikPaneli;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "SicaklikPaneli_TekKopya", out bool ilkKopya);
        if (!ilkKopya)
        {
            MessageBox.Show("Dragon Saj zaten çalışıyor (görev çubuğunun sağ alt köşesine bakın).",
                "Dragon Saj", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Beklenmeyen hatalar sessizce kaybolmasın: günlüğe yaz
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HataGunlugu.Write("Arayüz iş parçacığı", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HataGunlugu.Write("İşlenmeyen hata", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HataGunlugu.Write("Gözlenmeyen görev hatası", e.Exception);
            e.SetObserved();
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp());
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}

/// <summary>
/// Küçük hata günlüğü: %AppData%\SicaklikPaneli\hatalar.log. Dosya sınırı aşınca bir önceki
/// günlük ".old" olarak saklanır; böylece toplam boyut sınırlı kalır. Hiçbir zaman istisna fırlatmaz.
/// </summary>
internal static class HataGunlugu
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SicaklikPaneli");
    private static readonly string FilePath = Path.Combine(Dir, "hatalar.log");

    public static void Write(string context, Exception? ex) =>
        Write(ex == null ? context : $"{context}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
