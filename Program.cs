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

    /// <summary>Aynı ileti bu süre içinde yeniden gelirse yazılmaz, yalnızca sayılır.</summary>
    private const long RepeatWindowMs = 60000;
    private static string? _lastKey;
    private static long _lastAt;
    private static int _suppressed;

    public static void Write(string context, Exception? ex)
    {
        if (ex == null) { WriteCore(context, context, null); return; }
        // Aynı hata: bağlam + tür + ileti + yığının ilk satırı (tam yığın izi her seferinde yazılmasın)
        string? stack = ex.StackTrace;
        int nl = stack?.IndexOf('\n') ?? -1;
        string first = stack == null ? "" : (nl >= 0 ? stack[..nl] : stack).Trim();
        WriteCore($"{context}|{ex.GetType().FullName}|{ex.Message}|{first}", context, ex);
    }

    public static void Write(string message) => WriteCore(message, message, null);

    private static void WriteCore(string key, string context, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (key == _lastKey && now - _lastAt < RepeatWindowMs) { _suppressed++; return; }

                string time = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ";
                string text = time + (ex == null ? context : $"{context}: {ex}") + Environment.NewLine;
                if (_suppressed > 0)
                    text = $"{time}(önceki ileti {_suppressed} kez daha tekrarlandı){Environment.NewLine}" + text;
                _lastKey = key;
                _lastAt = now;
                _suppressed = 0;

                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, text);
            }
        }
        catch { }
    }
}
