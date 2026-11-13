# Sıcaklık Paneli

Windows için küçük bir işlemci / ekran kartı izleme programı.

- **Görev çubuğu:** Saatin yanındaki simgede işlemci sıcaklığı sayı olarak görünür. Alt çizginin rengi sıcaklığa göre değişir (yeşil → sarı → turuncu → kırmızı).
- **Ana pencere:** İşlemci ve ekran kartı için sıcaklık, kullanım, güç tüketimi, frekans ve VRAM; son 3 dakikanın sıcaklık grafiği ve bellek kullanımı.
- **Oyun paneli:** Tam ekran bir oyun açıldığında işlemci ve ekran kartının sıcaklığını ve kullanımını gösteren küçük bir panel belirir.
- **Fan denetimi (MSI dizüstüler):** Fan devrini izleme, sürüklenebilir fan eğrisi, tam hız (Cooler Boost) ve sıcaklık eşiği aşılınca fanları otomatik tam hıza alan koruma.
- **Performans modu (MSI dizüstüler):** Turbo ve Dengeli arasında geçiş; MSI'ın performans seviyesini (Shift) ve Windows güç planını birlikte değiştirir.
- **Ekran kartı hız aşırtma (NVIDIA):** Çekirdek ve bellek saatine sınırlı ofset (en çok +150 / +300 MHz). Voltaja dokunmaz; program kapanınca sıfırlanır.
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

## Fan denetimi

Fan sayfası, MSI dizüstülerin gömülü denetleyicisine (EC) erişerek çalışır ve yalnızca adres düzeni doğrulanmış bellenim ailelerinde etkinleşir (`16U7EMS1`, `17E7EMS1`, `17E8EMS1`, `17F2EMS1`, `17F3EMS1`, `17F3EMS2`, `17F4EMS1`, `17F4EMS2`, `17F5EMS1`, `17F6EMS1`). Diğer cihazlarda sayfa devre dışı kalır ve hiçbir şey yazılmaz.

- İlk çalıştırmada EC'deki özgün eğri `%AppData%\SicaklikPaneli\fan_fabrika.json` dosyasına yedeklenir; **Fabrika** düğmesi bu eğriyi geri yükler.
- Eğriler güvenlik kurallarından geçer: eşikler artan sırada olmalı, fan hızı sıcaklık arttıkça düşemez ve en üst kademe %100'ün altına inemez.
- Her yazma geri okunarak doğrulanır.
- MSI Center gibi başka bir yazılım da fanları yönetiyorsa ayarların üzerine yazabilir.

Fan ayarlarını değiştirmek kendi sorumluluğunuzdadır.

## Kullanılan kütüphaneler

- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — sensör okuma
