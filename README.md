# Son Durak Launcher

[Son Durak](https://github.com/anilpaskap01-droid/Gokhan-Abi/tree/main/Son%20Durak) oyununun launcher'ı ve kurulumu (Türkçe / English).

- **Oyun:** launcher, oyunu `Gokhan-Abi` reposunun `Son Durak` klasöründen indirir ve yeni commit'lerde "Güncelleme var" der; sadece değişen dosyaları indirir (git SHA-1 ile doğrulanır).
- **Launcher'ın kendisi:** bu reponun `release/` klasöründeki derlenmiş launcher'dır. Launcher her açılışta bu dosyaları kendi dosyalarıyla karşılaştırır; fark varsa indirir, doğrular, kendini değiştirir ve yeniden başlar. Launcher'ı güncellemek = `node build.mjs` + `release/`'i bu repoya göndermek.
- **Görünüm:** Steam kütüphane sayfası gibi: büyük oyun görseli, OYNA / GÜNCELLE düğmesi, sürüm, oynama süresi ve son oynama, altta **Yenilikler**. Yenilikler commit mesajlarından değil, oyun reposundaki `Son Durak/news.json` dosyasından gelir: oyuncular için yazılmış notlar (sürüm, tarih, başlık, maddeler; TR + EN) ve "Oyun hakkında" metni. Yeni bir oyun sürümü çıkınca en üste bir not eklemek yeterli. Oynama süresi `%LOCALAPPDATA%\SonDurak\launcher.json`'da tutulur.
- **Dil:** üst çubuktaki TR / EN düğmeleri. Seçim `%LOCALAPPDATA%\SonDurak\launcher.json`'a kaydedilir ve oyunla eşitlenir (oyun `index.html?lang=..` ile açılır, oyunda değiştirilirse launcher'a bildirir). Kurulum penceresi de bu seçimi / Windows dilini kullanır.

## Klasörler

```
Launcher/   C# (.NET Framework 4.8) + WebView2: launcher penceresi, oyun penceresi, güncelleyici
Setup/      yönetici izni istemeyen kurulum (payload.zip gömülü)
release/    derlenmiş launcher: launcher'lar kendini buradan günceller (anahtar İÇERMEZ)
build.mjs   derleme: release/ + dist/SonDurak-Setup.exe (10 MB altı olmalı)
```

## Derleme

```
node build.mjs
```

Gerekenler: .NET SDK, `SONDURAK_READ_TOKEN` ortam değişkeninde **sadece okuma izinli** (Contents: Read-only) bir GitHub anahtarı; `Gokhan-Abi` ve `Son-durak-Launcher` repolarına erişebilmeli. Derleme anahtarın iki repoyu da okuyabildiğini ve hiçbirine yazamadığını dener, sonra onu kurulum paketine ayrı bir `token.bin` olarak koyar (exe'nin içinde değildir; derleme bunu kontrol eder). `token.bin` ve `dist/` asla repoya gönderilmez. Anahtarın süresi dolarsa yeni anahtarla setup'ı yeniden derleyip kurmak gerekir.

## Test bayrakları

- `SonDurakLauncher.exe --selftest-install <json>`: oyunu başsız indir / güncelle
- `SonDurakLauncher.exe --selftest-selfupdate <json>`: launcher'ı bu reponun `release/`'i ile güncelle (yeniden başlatmadan)
- `SonDurakLauncher.exe --shot-launcher <png>` / `--shot-game <png>`
- `SONDURAK_INSTANCE=<ad>`: açık bir launcher'ın yanında ikinci bir test kopyası çalıştır
- `SonDurak-Setup.exe --silent --test --dir <klasör>` (dikkat: kurulum açık launcher'ları kapatır)
