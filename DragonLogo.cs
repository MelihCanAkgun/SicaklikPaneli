using System.Drawing.Drawing2D;

namespace SicaklikPaneli;

/// <summary>
/// Dragon Saj logosu: kanatları kırılmış, gözleri çarpı, dili dışarıda, kafasını yere koymuş kırmızı bir
/// ejderha (pixel art). Uygulama simgesi (DragonSaj.ico, tools/IconGen ile üretilir) ve pencere başlığındaki
/// logo aynı piksel verisinden çizilir.
/// </summary>
public static class DragonLogo
{
    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['K'] = Color.FromArgb(30, 10, 14),      // kontur
        ['R'] = Color.FromArgb(214, 40, 57),     // kırmızı
        ['D'] = Color.FromArgb(142, 21, 38),     // koyu kırmızı
        ['L'] = Color.FromArgb(255, 107, 94),    // ışık
        ['B'] = Color.FromArgb(244, 162, 89),    // karın
        ['b'] = Color.FromArgb(201, 123, 60),    // karın gölgesi
        ['W'] = Color.FromArgb(125, 29, 44),     // kanat zarı (arka)
        ['w'] = Color.FromArgb(168, 40, 59),     // kanat zarı (ön)
        ['H'] = Color.FromArgb(238, 221, 187),   // kemik / boynuz
        ['h'] = Color.FromArgb(184, 162, 126),   // kemik gölgesi
        ['T'] = Color.FromArgb(255, 126, 179),   // dil
        ['t'] = Color.FromArgb(201, 67, 126),    // dil gölgesi
        ['E'] = Color.FromArgb(250, 236, 236),   // göz akı
        ['S'] = Color.White,                     // diş
        ['G'] = Color.FromArgb(80, 0, 0, 0),     // yer gölgesi
    };

    /// <summary>32x32: kanatlardan kafaya kadar tüm ejderha.</summary>
    public static readonly string[] Large =
    {
        "................................",
        "................................",
        "................................",
        "................................",
        ".......KKK......................",
        "......KKHHK.....KK..............",
        ".....KKKwHK....KDWK.............",
        "....KHwwwHK...KWDDDKK...........",
        "...KHwwwwHK...KDDWWDDK..........",
        "..KHHwwwwHK...KDWWWWWDK.........",
        ".KwhwwwwwHK...KDWWWKKK..........",
        ".KHhwwwwwHK..KWKWWWK............",
        ".KHhwwwwwHK..KKHKKK.............",
        ".KHhwwwKwHHK.KDKHhK.............",
        ".KHhwwwKwwHK.KDKHHhK............",
        "KwHKhwwwwwHKKWDWKHhKKKKKK.......",
        "KHwwhwwwwwHKKWDWKRHhLLLLLK......",
        "KHwwhwwwwwHKKWDKLRRRRRRRRLK.....",
        "KHwwhKwwKwHKKDKLRRRRKKRKKRLKK...",
        "KHwwKDKKDKKDKKKRRRRRRKKKRRRLLK..",
        "KHKKDDKDDKLRLLLRRRRRRKKKRRRRKLK.",
        ".KKKRRLRRLRRRRRRRRRRKKRKKRRRRRK.",
        ".KLLRRRRRRRRRRRRRRRRKRRRKRRRRRK.",
        "KLRRRRRRRRRRRRRRRRRRRRRRRRRRRRLK",
        "KRRRRRRRRRRRRRRRRRRRRRRRRRKKKKK.",
        "KRRRRRRRRRRRRRRRDRRRRKKKKKSTTKK.",
        "KRRRRRBBBBBBBRRRKRRRRRRSRRRRTTK.",
        "KDRBBBBBBBBBBBRRLRRRDDDDDDDDDTTK",
        ".KDbBBBBBBBBBDRRRDDDKKKKKKKKKTtK",
        "..KKbbbbbbbbbKDDDKKKGGGGGGGKtTtK",
        ".GGGKKKKKKKKKGKKKGGGGGGGGGGGKKK.",
        "........GGGGGGGGGGGGGGGG........",
    };

    /// <summary>32x32: yalnızca kafa (görev çubuğu ve büyük simgeler).</summary>
    public static readonly string[] Head =
    {
        "................................",
        "................................",
        "................................",
        "........K.......................",
        ".......KHK......................",
        ".K.....KHK......................",
        "KHK....KHHK.....................",
        ".KHK...KHHK.....................",
        ".KhHK..KhHHK....................",
        "..KHHK..KHHK....................",
        "..KhHHKKLRRKKK..................",
        "...KHRLLRRRLLLKKK...............",
        "...KRRRRRRRRRRLLLKKK............",
        "..KLRRRRRRRDDDDDDLLLKKKK........",
        "KKLRRRRRRRDRRRRRRDDRLLLLKKKK....",
        "DDRRRRRRRRRRKKRKKRRRRRRRLLLLK...",
        "KDRRRRRRRRRRRKKKRRRRRRRRRRRRLK..",
        "KRRRRRRRRRRRRKKKRRRRRRRRRRRKKLK.",
        "KRRRRRRRRRRRKKRKKRRRRRRRRRRRRRLK",
        "KRRRRRRRRRRRKRRRKRRRRRRRRRRRRRDK",
        "KDRRRRRRRRRRRRRRRRRRRRRRRRRRRRK.",
        "DDRRRRRRRRRRRRRRRRRRRRRRRRRRRDK.",
        "KKDRRRRRRRRRRRRRDDDDDSDDDSDDDK..",
        "..KDRRRRRRRRRRKKKKKKKKKKKKKKK...",
        "...KDRRRRRRRRRRRLLLLKKKKttTTTK..",
        "....KDRRRRRRRRRRRRRRLLLSLLLLTTK.",
        ".....KRRRRRRRRRRRRRRRRRRRRRRKTTK",
        ".....KDDDDDDDDDDDDDDDDDDDDDDKTTt",
        "......KKKKKKKKKKKKKKKKKKKKKKKKtt",
        ".GGGGGGGGGGGGGGGGGGGGGGGGGGGKKKK",
        "...GGGGGGGGGGGGGGGGGGGGGGGGGG...",
        "................................",
    };

    /// <summary>16x16: küçük simge boyutları için sade kafa.</summary>
    public static readonly string[] Small =
    {
        "................",
        ".K..............",
        "KHK.............",
        ".KHK............",
        ".KHHKKKK........",
        "..KHLLLLKK......",
        "..KRRRRRLLKK....",
        ".KLRRRKRKRLLK...",
        "KLRRRRRKRRRRLK..",
        "KRRRRRKRKRRRKLK.",
        "KDRRRRRRRRRRRRK.",
        ".KRRRRRRRRRRRRLK",
        ".KDRRRRRKKKKKKK.",
        "..KDDDDDDDDDTTK.",
        "...KKKKKKKKKKTtK",
        ".GGGGGGGGGGGGKK.",
    };

    private static readonly Lazy<Bitmap> Cached = new(() => Render(32, headOnly: false));

    /// <summary>
    /// Piksel verisini verilen boyutta (en yakın komşu, keskin) bir bitmap'e çizer.
    /// headOnly: simge için yalnızca kafa; false: pencere içindeki tüm ejderha.
    /// </summary>
    public static Bitmap Render(int size, bool headOnly = true)
    {
        var grid = size < 32 ? Small : headOnly ? Head : Large;
        int n = grid.Length;
        // Pikseller önce bir diziye yazılıp tek seferde kopyalanır (piksel başına SetPixel kilitlemesi yerine).
        // Değerler SetPixel'in yazacağıyla birebir aynıdır (bilinmeyen karakter: Color.Transparent).
        int transparent = Color.Transparent.ToArgb();
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            string row = grid[y * n / size];
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = Palette.TryGetValue(row[x * n / size], out var col) ? col.ToArgb() : transparent;
        }

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < size; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * size, data.Scan0 + y * data.Stride, size);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>Logoyu bir alana keskin (bulanıklaşmadan) çizer.</summary>
    public static void Draw(Graphics g, RectangleF dest)
    {
        var bmp = Cached.Value;
        var oldMode = g.InterpolationMode;
        var oldOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(bmp, dest);
        g.InterpolationMode = oldMode;
        g.PixelOffsetMode = oldOffset;
    }

    /// <summary>Programa gömülü çok boyutlu simge (DragonSaj.ico).</summary>
    public static Icon LoadIcon()
    {
        using var stream = typeof(DragonLogo).Assembly.GetManifestResourceStream("DragonSaj.ico");
        if (stream != null) return new Icon(stream);
        // Yedek yol: GetHicon bitmap'i kopyaladığı için geçici bitmap hemen bırakılabilir
        using var bmp = Render(32);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
