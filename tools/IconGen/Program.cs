using System.Drawing.Imaging;
using SicaklikPaneli;

// Çok boyutlu .ico yazar. 48 piksele kadar klasik 32 bit BMP (her Windows sürümü okur),
// daha büyükleri PNG olarak saklanır (Windows Vista ve sonrası).
string output = args.Length > 0 ? args[0] : "DragonSaj.ico";
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

var images = sizes.Select(s => (size: s, data: s <= 48 ? Dib(s) : Png(s))).ToList();

using var fs = File.Create(output);
using var w = new BinaryWriter(fs);
w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
int offset = 6 + 16 * images.Count;
foreach (var (size, data) in images)
{
    w.Write((byte)(size >= 256 ? 0 : size));
    w.Write((byte)(size >= 256 ? 0 : size));
    w.Write((byte)0); w.Write((byte)0);
    w.Write((short)1); w.Write((short)32);
    w.Write(data.Length); w.Write(offset);
    offset += data.Length;
}
foreach (var (_, data) in images) w.Write(data);
Console.WriteLine($"{output}: {string.Join(", ", sizes)} px");

static byte[] Png(int size)
{
    using var bmp = DragonLogo.Render(size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(int size)
{
    using var bmp = DragonLogo.Render(size);
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    int maskStride = ((size + 31) / 32) * 4;
    w.Write(40); w.Write(size); w.Write(size * 2);      // BITMAPINFOHEADER (yükseklik: renk + maske)
    w.Write((short)1); w.Write((short)32); w.Write(0);
    w.Write(size * size * 4 + maskStride * size);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = size - 1; y >= 0; y--)                 // alttan üste, BGRA
        for (int x = 0; x < size; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    for (int y = size - 1; y >= 0; y--)                 // AND maskesi: saydam pikseller 1
    {
        var row = new byte[maskStride];
        for (int x = 0; x < size; x++)
            if (bmp.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(row);
    }
    return ms.ToArray();
}
