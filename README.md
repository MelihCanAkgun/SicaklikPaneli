# Sıcaklık Paneli

Windows için küçük bir işlemci / ekran kartı izleme programı.

- **Görev çubuğu:** Saatin yanındaki simgede işlemci sıcaklığı sayı olarak görünür. Alt çizginin rengi sıcaklığa göre değişir (yeşil → sarı → turuncu → kırmızı).
- **Ana pencere:** İşlemci ve ekran kartı için sıcaklık, kullanım, güç tüketimi, frekans ve VRAM; son 3 dakikanın sıcaklık grafiği ve bellek kullanımı.
- **Oyun paneli:** Tam ekran bir oyun açıldığında işlemci ve ekran kartının sıcaklığını ve kullanımını gösteren küçük bir panel belirir.
- **Isıl kısıtlama uyarısı:** İşlemci ya da ekran kartı ısıdan dolayı hız kestiğinde (thermal throttling) panelde uyarı işareti çıkar.

```
CPU   72°C   45%
GPU   65°C   80%
```

## Gereksinimler

- Windows 10 / 11 (64 bit)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- [PawnIO](https://pawnio.eu/) sürücüsü — işlemci sıcaklığını okumak için: `winget install namazso.PawnIO`
- (İsteğe bağlı) [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) — panelin özel (exclusive) tam ekran oyunlarda da görünmesi için

Program sensörlere erişebilmek için yönetici izniyle çalışır.

## Derleme

```
dotnet build -c Release
```

Çıktı: `bin\Release\net9.0-windows\win-x64\SicaklikPaneli.exe`

## Kullanım

| İşlem | Nasıl |
|---|---|
| Ana pencereyi aç | Simgeye sol tık |
| Oyun paneli modunu değiştir (otomatik / her zaman / kapalı) | `Ctrl+Shift+F10` veya ana pencerenin altındaki düğmeler |
| Panel konumu, saydamlık | Simgeye sağ tık |
| Windows ile başlat | Simgeye sağ tık → *Windows ile başlat* |

RTSS çalışıyorsa oyun içi gösterge RTSS üzerinden çizilir; konumu ve boyutu RTSS ayarlarından değiştirilir.

## Kullanılan kütüphaneler

- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — sensör okuma
