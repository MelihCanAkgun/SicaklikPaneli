<div align="center">

# Dragon Saj

**Windows için hafif CPU / GPU sıcaklık izleyici ve oyun içi panel**

İşlemci ve ekran kartı sıcaklığını görev çubuğunda, ana pencerede ve oyunun üstünde gösterir.
MSI dizüstülerde fan eğrisi, performans modu ve sınırlı NVIDIA hız aşırtma da sunar.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D6)
![.NET](https://img.shields.io/badge/.NET-9.0%20Desktop-512BD4)
![Dil](https://img.shields.io/badge/C%23-WinForms-239120)

</div>

> Depo adı `SicaklikPaneli`, programın güncel adı **Dragon Saj**'dır (çıktı dosyası `DragonSaj.exe`).

---

## Özellikler

- **Görev çubuğunda canlı sıcaklık:** Saatin yanındaki simgede işlemci sıcaklığı sayı olarak görünür. Alttaki çizginin rengi sıcaklığa göre değişir (yeşil → sarı → turuncu → kırmızı). Fareyle üzerine gelince CPU ve GPU özeti çıkar.
- **Ana pencere:** CPU ve GPU için sıcaklık, kullanım, güç tüketimi, en yüksek çekirdek frekansı, VRAM ve sistem belleği; son 3 dakikanın sıcaklık grafiği.
- **Oyun içi panel:** Tam ekran bir uygulama öne geldiğinde CPU/GPU sıcaklığı ve kullanımını gösteren küçük, yarı saydam bir panel belirir. Panel tıklamaları geçirir, odak çalmaz.
  - Kenarlıksız / pencereli tam ekranda kendi penceresiyle çizilir.
  - [RivaTuner Statistics Server (RTSS)](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) kuruluysa gösterge RTSS üzerinden oyunun içine çizilir; böylece **özel (exclusive) tam ekran** oyunlarda da görünür. Kuruluysa RTSS otomatik başlatılır.
- **Isıl kısıtlama uyarısı:** İşlemci ya da ekran kartı ısı yüzünden hız kestiğinde (thermal throttling) değer kırmızıya döner ve uyarı işareti çıkar.
  - Intel işlemcilerde MSR durum kayıtları, diğerlerinde TjMax yakınlığı; NVIDIA kartlarda NVML kısıtlama nedenleri kullanılır.
- **Fan denetimi (yalnızca desteklenen MSI dizüstüler):** Fan devri izleme, sürüklenebilir fan eğrisi, sabit devir (1500–6200 RPM, CPU ve GPU fanı ayrı ayrı), tam hız (Cooler Boost) ve eşik aşılınca fanları otomatik tam hıza alan koruma.
- **Performans modu (MSI dizüstüler):** *Turbo* ve *Dengeli* arasında geçiş; MSI performans seviyesi (Shift) ile Windows güç planını birlikte değiştirir.
- **NVIDIA hız aşırtma:** Çekirdek saatine en çok +150 MHz, bellek saatine en çok +300 MHz ofset. Voltaja dokunmaz; program kapanınca ofset sıfırlanır, program düzgün kapanmazsa bir sonraki açılışta ofset uygulanmaz.
- **Kısayol:** `Ctrl+Shift+F10` ile oyun paneli modu değişir (otomatik → her zaman → kapalı).
- **Windows ile başlatma:** Görev Zamanlayıcı'ya "en yüksek ayrıcalıkla, oturum açılınca" görevi eklenir; böylece her açılışta UAC sorusu çıkmaz.
- **Tek kopya:** Program ikinci kez açılırsa uyarı verir.
- Yüksek DPI (Per-Monitor V2) desteği.

Oyun paneli örneği:

```
CPU   72°C   45%
GPU   65°C   80%
```

---

## Gereksinimler

| Gereksinim | Not |
|---|---|
| **Windows 10 veya 11, 64 bit** | Yalnızca `win-x64` için derlenir. |
| **[.NET 9 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/9.0)** | Program çerçeveye bağımlı (framework-dependent) derlenir. `winget install Microsoft.DotNet.DesktopRuntime.9` |
| **Yönetici izni** | Uygulama bildirimi `requireAdministrator` ister; sensörlere, MSR'a ve EC'ye erişim için gereklidir. Her açılışta UAC onayı çıkar (Windows ile başlat seçeneği hariç). |
| **[PawnIO](https://pawnio.eu/) sürücüsü** | CPU sıcaklığı, MSR ve EC erişimi için. `winget install namazso.PawnIO` — kurulu değilse program uyarı balonu gösterir. |
| NVIDIA sürücüsü *(isteğe bağlı)* | GPU kısıtlama uyarısı (NVML) ve hız aşırtma (NVAPI) için. |
| RTSS *(isteğe bağlı)* | Panelin özel tam ekran oyunlarda da görünmesi için. |

---

## Derleme ve çalıştırma

Hazır derlenmiş bir sürüm yayımlanmamıştır; programı kullanmak için kaynaktan derlemeniz gerekir. Derlemek için [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) gerekir.

```powershell
git clone https://github.com/MelihCanAkgun/SicaklikPaneli.git
cd SicaklikPaneli

# Yerel deneme
dotnet build -c Release
# Çıktı: bin\Release\net9.0-windows\win-x64\DragonSaj.exe

# Tek dosyalık exe (çerçeveye bağımlı)
dotnet publish -c Release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# Çıktı: bin\Release\net9.0-windows\win-x64\publish\DragonSaj.exe
```

Runtime kurmadan çalışacak, kendi kendine yeterli (self-contained) bir exe derlemek için:

```powershell
dotnet publish -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Çalıştırmadan önce PawnIO'yu (ve self-contained derlemediyseniz .NET 9 Desktop Runtime'ı) kurun:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.9
winget install namazso.PawnIO
```

Ardından `DragonSaj.exe`'yi çalıştırıp yönetici iznini onaylayın. Exe dijital olarak imzalı olmadığından Windows SmartScreen uyarı gösterebilir.

Logo dosyası (`DragonSaj.ico`) `DragonLogo.cs` içindeki piksel verisinden üretilir. Logoyu değiştirdikten sonra:

```powershell
dotnet run --project tools\IconGen -- DragonSaj.ico
```

---

## Kullanım

| İşlem | Nasıl |
|---|---|
| Ana pencereyi aç | Tepsi simgesine sol tık |
| Oyun paneli modunu değiştir (otomatik / her zaman / kapalı) | `Ctrl+Shift+F10`, ana pencerenin altındaki düğmeler ya da sağ tık → *Oyun paneli* |
| Panel konumu (dört köşe) | Sağ tık → *Panel konumu* |
| Panel saydamlığı (%50 / %65 / %80 / %95) | Sağ tık → *Panel saydamlığı* |
| Performans modu | Sağ tık → *Performans modu* ya da *Performans* sayfası |
| Windows ile başlat | Sağ tık → *Windows ile başlat* |

- Sensörler saniyede bir okunur; panelin görünürlüğü saniyede iki kez denetlenir.
- RTSS çalışıyorsa oyun içi gösterge RTSS ile çizilir; konumu ve boyutu RTSS ayarlarından değiştirilir.
- `--gizli` argümanıyla başlatılırsa ana pencere açılmaz, yalnızca tepsi simgesi görünür.

### Ayar dosyaları

| Dosya | İçerik |
|---|---|
| `%AppData%\SicaklikPaneli\ayarlar.json` | Panel modu, konum, saydamlık, fan / performans / hız aşırtma ayarları |
| `%AppData%\SicaklikPaneli\fan_fabrika.json` | İlk çalıştırmada EC'den yedeklenen özgün fan eğrisi |
| `%AppData%\SicaklikPaneli\hatalar.log` | Beklenmeyen hataların günlüğü (sorun bildirirken işe yarar) |

Programı tamamen kaldırmak için: *Windows ile başlat* seçeneğini kapatın (ya da `schtasks /delete /tn DragonSaj /f`), program klasörünü ve `%AppData%\SicaklikPaneli` klasörünü silin.

---

## Fan denetimi hakkında

Fan sayfası, MSI dizüstülerin gömülü denetleyicisine (EC) doğrudan erişerek çalışır ve **yalnızca adres düzeni doğrulanmış bellenim ailelerinde** etkinleşir:

`16U7EMS1`, `17E7EMS1`, `17E8EMS1`, `17F2EMS1`, `17F3EMS1`, `17F3EMS2`, `17F4EMS1`, `17F4EMS2`, `17F5EMS1`, `17F6EMS1`

Diğer cihazlarda sayfa devre dışı kalır ve EC'ye hiçbir şey yazılmaz.

- **Fabrika** düğmesi, ilk çalıştırmada yedeklenen özgün eğriyi geri yükler.
- Eğriler güvenlik kurallarından geçer: eşikler artan sırada olmalı, fan hızı sıcaklık arttıkça düşemez, en üst kademe %100'ün altına inemez.
- **Sabit devir** modunda EC doğrudan RPM kabul etmediği için hız değeri birkaç saniyede bir ölçülen devre göre düzeltilir; 88 °C üstünde fanlar her durumda tam hıza çıkar.
- Her yazma geri okunarak doğrulanır.

> ⚠️ Fan, performans ve hız aşırtma ayarlarını değiştirmek **kendi sorumluluğunuzdadır**. Yazılım "olduğu gibi" sunulur.

---

## Bilinen sınırlamalar

- **Yalnızca Windows x64.** ARM64 ve 32 bit desteklenmez.
- **Yönetici izni zorunludur**; her elle açılışta UAC onayı çıkar.
- **PawnIO olmadan CPU sıcaklığı okunamaz**; ayrıca bazı güvenlik yazılımları / Bellek Bütünlüğü (HVCI) ayarları düşük seviyeli sürücüleri engelleyebilir.
- **Özel tam ekran** oyunlarda kendi panel penceresi görünmeyebilir; bu durumda RTSS gerekir. Bazı hile önleyici (anti-cheat) sistemler bindirmelerle sorun çıkarabilir.
- Tam ekran algılaması, öndeki pencerenin ekranı tamamen kaplamasına bakar; tam ekran video ya da tarayıcı da paneli açabilir.
- **Fan denetimi ve performans modu yalnızca listelenen MSI bellenimlerinde** çalışır. MSI Center gibi başka bir yazılım da fanları yönetiyorsa ayarların üzerine yazabilir.
- **Hız aşırtma yalnızca NVIDIA** kartlarda ve ilk fiziksel GPU'da çalışır; AMD / Intel desteklenmez. Sürücü ofseti kendiliğinden sıfırlayabilir.
- **Isıl kısıtlama algılaması:** CPU için ayrıntılı algılama yalnızca Intel'de (MSR); AMD'de TjMax yakınlığına göre tahmin edilir. GPU için yalnızca NVIDIA.
- Birden fazla ekran kartı varsa harici kart tercih edilir (NVIDIA > AMD > Intel); yalnızca biri gösterilir.
- Arayüz yalnızca Türkçedir.

---

## Teknik ayrıntılar

| Konu | Uygulama |
|---|---|
| Çerçeve | .NET 9, Windows Forms, `net9.0-windows`, `win-x64` |
| Sensörler | LibreHardwareMonitorLib (`Computer` + `IVisitor`) |
| Oyun paneli | Kenarlıksız, `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_TOPMOST` pencere; `SetWindowPos` ile en üstte tutulur |
| RTSS | `RTSSSharedMemoryV2` paylaşımlı belleğine OSD metni yazılır |
| Fan / Shift | LibreHardwareMonitor'ın EC G/Ç sınıfı üzerinden MSI EC adresleri |
| Isıl kısıtlama | Intel MSR (`IA32_THERM_STATUS`, `IA32_PACKAGE_THERM_STATUS`), NVIDIA NVML |
| Hız aşırtma | NVAPI Pstates20 arayüzü |
| Güç planı | `powercfg /setactive` |
| Otomatik başlatma | `schtasks` (oturum açılışında, en yüksek ayrıcalık) |

---

## Üçüncü taraf bileşenler

| Bileşen | Kullanım | Lisans | Dağıtım |
|---|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 | Sensör okuma, EC ve MSR erişimi | [MPL-2.0](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE) | NuGet paketi; derleme çıktısında DLL olarak bulunur |
| LibreHardwareMonitorLib'in geçişli NuGet bağımlılıkları | — | Her paketin kendi lisansı | Derleme çıktısında DLL olarak bulunur |
| [PawnIO](https://pawnio.eu/) | Çekirdek düzeyi donanım erişimi | Proje sayfasına bakın | **Paketlenmez**, kullanıcı ayrıca kurar |
| [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) | Oyun içi OSD | Ücretsiz, kapalı kaynak | **Paketlenmez**; yalnızca paylaşımlı belleği kullanılır |
| NVIDIA NVML / NVAPI | Kısıtlama algılama, hız aşırtma | NVIDIA sürücüsünün parçası | **Paketlenmez**; sürücüyle gelir |

LibreHardwareMonitorLib, **Mozilla Public License 2.0** ile lisanslanmıştır. Bu proje kütüphaneyi değiştirmeden kullanır; kütüphanenin kaynak kodu yukarıdaki bağlantıda açıktır.

---

## Lisans

Bu depoda şu an bir lisans dosyası bulunmuyor. Üçüncü taraf bileşenler kendi lisanslarına tabidir (bkz. yukarıdaki tablo).

Dragon Saj; MSI, NVIDIA veya RivaTuner ile bağlantılı değildir. Adı geçen markalar sahiplerine aittir.
