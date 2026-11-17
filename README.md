# BatteryRings

Kablosuz cihazların pil seviyesini Windows görev çubuğunda **tek bir halka simgesiyle** gösterir.
Simgenin üzerine gelince ya da tıklayınca her cihazın ayrıntısı açılır.

## Desteklenen cihazlar

**Kendiliğinden (ayar gerekmez):**
- Pil seviyesini Windows'a bildiren **her Bluetooth cihaz**: kulaklık, mouse, klavye, oyun kolu.
  Windows Ayarlar > Bluetooth'ta yüzdesi görünüyorsa burada da görünür.
- **Xbox ve XInput uyumlu kablosuz oyun kolları** (Windows yalnızca 4 kademe verir: Boş / Düşük / Orta / Dolu).

**Özel destek (daha ayrıntılı):**

| Cihaz | Ne gösterir |
|---|---|
| Razer Barracuda X (2022), 2.4 GHz alıcı | Pil ve şarj durumu |
| ATK A9 Ultimate (ve aynı Compx firmware'li ATK mouse'lar), alıcı ya da kablo | Voltajdan hesaplanan, zıplamayan pil yüzdesi |
| CMF Buds Pro 2 (Nothing) | Sol / sağ / kutu ayrı ayrı |

- Simgede ilk üç cihaz iç içe halka olarak görünür; panelde hepsi listelenir.
- Sağ tık > **Cihazlar** menüsünden istemediğin cihazı gizleyebilirsin.
- Bağlı olmayan cihaz gösterilmez; tek cihaz varsa halkanın ortasında o cihazın simgesi durur.
- Şarj olurken halka sarı olur ve üzerinde bir ışık döner; %15'in altında kırmızı yanıp söner.
- %20, %10 ve %5'te uyarı bildirimi gelir.

## Kurulum

1. [Releases](../../releases) sayfasından `BatteryRings.exe`'yi indir.
2. Çalıştır, "kurulsun mu?" sorusuna **Evet** de.

Kendini `%LOCALAPPDATA%\Programs\BatteryRings` altına kurar, Windows ile birlikte açılır ve Başlat menüsüne eklenir.
Yönetici izni gerekmez. Yeni sürümü aynı şekilde çalıştırmak eskisinin üzerine kurar.
Kaldırmak için simgeye sağ tıklayıp **Kaldır**'ı seç.

> İmzasız bir exe olduğu için Windows SmartScreen ya da antivirüs uyarı verebilir. Kaynak kod bu repoda;
> istersen exe'yi kendin derleyebilirsin.

## Derleme

Ek bir şey kurmaya gerek yok; Windows'ta hazır gelen .NET Framework 4 derleyicisi kullanılır:

```bat
build.cmd
```

Çıktı: `dist\BatteryRings.exe`. Komut satırı seçenekleri:

| Seçenek | Ne yapar |
|---|---|
| `/portable` | Kurmadan çalıştırır |
| `/install`, `/uninstall` | Sessiz kurulum / kaldırma |
| `/test` | Cihazları bir kez okuyup `test_sonuc.txt`'ye yazar |

## Nasıl çalışıyor

| Cihaz | Yol |
|---|---|
| Razer Barracuda X | Alıcının HID arabirimi (1532:0552, usage page 0xFF00): pil ve şarj kablosu sorguları |
| ATK mouse | Compx HID raporları (VID 373B, usage page 0xFF02). Firmware'in yüzdesi zıpladığı için yüzde **voltajdan** hesaplanır (LiHV eğrisi, tam dolu ≈ 4,35 V) ve filtrelenir; şarjda gerçek şarj hızına göre yükselir |
| CMF Buds | Bluetooth RFCOMM üzerinden Nothing protokolü (servis `aeac4a03-dff5-498f-843a-34487cf133eb`, pil komutu `0xC007`) |

| Bluetooth (genel) | Windows'un aygıt özelliği `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` (SetupAPI) |
| Oyun kolları | `XInputGetBatteryInformation` |

Kod: `src/Devices.cs` (HID cihazları), `src/Buds.cs` (Bluetooth kulaklık), `src/Generic.cs` (genel Bluetooth ve XInput),
`src/UI.cs` (simge ve panel), `src/Program.cs` (kurulum).
Yeni bir özel cihaz eklemek için `UI.cs`'te bir `Slot` tanımlayıp `Refresh` içinde okumak yeterli.

## Gizlilik ve güvenlik

- **İnternete hiç bağlanmaz**, veri toplamaz ve hiçbir şey göndermez. Ağ kodu yoktur; tek iletişim yerel USB/HID ve Bluetooth cihazlarıyladır.
- Yönetici izni istemez; yalnızca kullanıcının kendi klasörüne ve kayıt defterine (`HKCU`) yazar:
  `%LOCALAPPDATA%\Programs\BatteryRings`, `HKCU\Software\BatteryRings` (gizlenen cihazlar, mouse'un son değeri) ve otomatik başlatma kaydı. **Kaldır** hepsini siler.
- Cihazlara yalnızca **okuma** komutları gönderir; ayarlarını değiştirmez (mouse'ta EEPROM yazma komutu hiç kullanılmaz).
- Sistem DLL'leri yalnızca `System32`'den yüklenir; exe'nin yanına konan sahte bir DLL çalıştırılmaz.
- Cihazlardan gelen veriler sabit boyutlu tamponlarda, sınır kontrolüyle ayrıştırılır.

## Teşekkürler

Nothing / CMF protokolü için [BudsLink](https://github.com/maniacx/BudsLink) ve
[Gadgetbridge](https://codeberg.org/Freeyourgadget/Gadgetbridge) projelerinin belgelerinden yararlanıldı.

## Lisans

GPL-3.0
