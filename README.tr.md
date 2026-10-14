# BatteryTray

[English](README.md) · **Türkçe**

Kablosuz kulaklık, fare, klavye ve AirPods'un pil seviyesini Windows sistem tepsisinde
gösteren küçük bir uygulama. Üretici yazılımı (iCUE, Armoury Crate, GG …) gerektirmez;
cihazlarla doğrudan USB HID ve Bluetooth LE üzerinden konuşur.

## Özellikler

- **Tepsi simgesi:** Kulaklığın pil yüzdesi, küçük boyutta bile okunacak piksel rakamlarla.
  Bir cihaz şarj oluyorsa simge, yüzde ile şarj olan cihazın yeşil simgesi arasında döner.
- **Her cihaz için ayrı tepsi simgesi:** sağ tık → **Tepside göster** menüsünden istediğin cihazları işaretle.
  İşaretlenen her cihaz kendi yüzdesiyle ayrı bir simge alır (HWiNFO'daki ayrı sensörler gibi);
  cihaz adı için simgenin üzerine gel. Hepsinin işaretini kaldırırsan tek kulaklık simgesine dönülür.
  Windows 11'de yeni tepsi simgeleri `^` taşma alanında başlar: görev çubuğuna sürükle.
- **Pil paneli:** Simgeye tıklayınca, tıklanan yerin üstünde açılan koyu panel.
  - *Cihazlarım:* bağlı cihazların adı, bağlantı türü, yüzdesi ve pil çubuğu.
    Bilgi vermeyen cihazlar (cihazı kapalıyken takılı duran alıcı) listelenmez; tek istisna kulaklığı
    olmadan takılı HS80 alıcısıdır, bunu belirtir. Uykuya geçen cihaz son değerini göstermeye devam eder.
  - *Yakındakiler:* çevredeki Bluetooth LE cihazları; AirPods/Beats için sol, sağ ve kutu pili
  - *Fare ayrıntıları:* ATK/VXE fareler için etkin DPI kademesi ve tarama (polling) hızı da gösterilir
- **Düşük pil bildirimi:** %10 ve %5'in altında Windows bildirimi (cihaz başına bir kez).
- **Windows ile başlat:** sağ tık menüsünden açılıp kapatılır, yönetici izni gerekmez.
- **Türkçe / İngilizce arayüz:** varsayılan dil İngilizce; sağ tık → **Language** menüsünden
  Türkçe seçilebilir ya da Windows'un diline uyması sağlanabilir.

## Desteklenen cihazlar

| Tür | Cihazlar | Not |
|---|---|---|
| Kulaklık | Corsair HS80 MAX, Virtuoso MAX, Void Wireless V2 | 2.4 GHz alıcı ya da USB kablo |
| Fare | ASUS ROG kablosuz fareler (Gladius III, Harpe, Keris, Chakram, Spatha …) | Alıcı ya da USB kablo |
| Fare | ATK VXE Dragonfly R1 serisi (R1 Pro Max, R1 SE+ …) | *Deneysel.* Alıcı ya da USB kablo; pil, etkin DPI ve tarama hızı. DPI / tarama hızı yalnızca farenin ayarları okunup doğrulanabilirse gösterilir, aşağıya bak |
| Klavye | Keychron K2 Max | **Yamalı firmware gerekir**, bkz. [`firmware/keychron-k2-max`](firmware/keychron-k2-max) |
| Klavye | ASUS ROG kablosuz klavyeler: ROG Falchion ve Omni Receiver ile bağlanan klavyeler (ör. Strix Scope II 96 Wireless) | Alıcı ya da USB kablo; diğer ROG klavyeler denenmedi |
| Kulak içi | AirPods, AirPods Pro, Beats | Bluetooth LE yayınından, 10'ar puanlık hassasiyet |
| Fare / klavye | Lightspeed / Unifying / Bolt alıcılı Logitech cihazlar (ör. G305) | *Deneysel.* Sadece voltaj ya da seviye bildiren cihazlar yaklaşık (~) gösterilir |

Listede olmayan markalar (Razer, SteelSeries, HyperX …) her biri farklı bir
protokol kullandığı için şimdilik görünmez. Katkılara açık.

**ATK / VXE fareler:** pil sorgusu, diğer projelerin gerçek donanımda doğruladığı kısım (VXE R1 Pro Max).
Fare her ayarı bir sağlama (checksum) değeriyle birlikte saklar; bu yüzden BatteryTray DPI ve tarama hızını
yalnızca değerler geçerli okunursa gösterir. Bellek düzeni farklı bir modelde bu alanlar tahmin edilmez,
sadece gösterilmez. Pil görünüp DPI / tarama hızı görünmüyorsa **Tanılama**'yı çalıştırıp log'u bir issue'ya ekle
(log, farenin ayar belleğinin ham ve salt okunur dökümünü içerir). BatteryTray fareye asla yazmaz.

## Kurulum

1. [Releases](../../releases) sayfasından `BatteryTray.exe`'yi indir.
2. Çalıştır. Simge sağ altta belirir (görünmüyorsa `^` okuna tıklayıp görev çubuğuna sürükle).
3. İstersen sağ tık → **Windows ile başlat**.

.NET kurmana gerek yok; exe kendi içinde her şeyi taşır.

> İlk çalıştırmada Windows SmartScreen "tanınmayan uygulama" uyarısı verebilir:
> **Ek bilgi → Yine de çalıştır**. Exe imzalı değil; kaynak kodu bu repoda.

## Kaynaktan derleme

[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) gerekir.

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
.\out\BatteryTray.exe
```

## Sorun giderme

Sağ tık menüsünde:

- **Tanılama:** bağlı tüm HID cihazlarını (üretici, ürün, arayüzler) ve desteklenen markaların
  cevaplarını log'a yazar, sonra log'u açar.
- **Logu aç:** `batterytray_log.txt` — cihaz arama ve hata ayrıntıları.

Yeni bir cihaz eklemek istiyorsan cihaz bağlıyken **Tanılama**'yı çalıştır ve log'u bir issue'ya ekle.

## Teşekkürler

Protokoller bu açık kaynak projelerden öğrenildi:

- [HeadsetControl](https://github.com/Sapd/HeadsetControl) — Corsair kablosuz kulaklık protokolü
- [hs80-max-control](https://github.com/jacohallum/hs80-max-control) — HS80 MAX pil ve şarj özellikleri
- [BatteryTool](https://github.com/1gcat/BatteryTool) ve [G-Helper](https://github.com/seerge/g-helper) — ASUS ROG fare pil sorgusu
- [battery-tray](https://github.com/mikgangal/battery-tray) — ASUS ROG klavye (Omni Receiver) pil sorgusu
- [k10he-battery-firmware](https://github.com/OutersGIT/k10he-battery-firmware) — Keychron 2.4 GHz pil yaması
- [OpenPods](https://github.com/adolfintel/OpenPods) — AirPods Bluetooth LE yayın biçimi
- [Solaar](https://github.com/pwr-Solaar/Solaar) — Logitech HID++ pil özellikleri
- [HaloBattery](https://github.com/HeyOkay/HaloBattery) — VXE R1 Pro Max pil sorgusu ve USB kimlikleri
- [python-pulsar-mouse-tool](https://github.com/andrewrabert/python-pulsar-mouse-tool) — Compx paket biçimi ve ayar belleği haritası (DPI, tarama hızı)
- [OpenMouse](https://github.com/OpenMouse-Project/mouse-protocol) — çapraz kontrol için kullanılan ATK/VXE protokol notları

## Lisans

[GPL-3.0](LICENSE). Keychron firmware yaması QMK türevi olduğu için GPL-2.0-or-later.
Bu proje Corsair, ASUS, Keychron, Logitech, Apple, ATK, VXE ya da SteelSeries ile bağlantılı değildir.
