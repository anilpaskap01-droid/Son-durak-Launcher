# Son Durak Launcher

**English:** the open-source Windows launcher and installer of [Son Durak](https://son-durak.pages.dev), a free co-op
anomaly-spotting game set in a night tram. The launcher installs and updates the game, keeps your stats,
achievements and anomaly collection, and has a peer-to-peer friends list (requests, chat, invites, parties).
It is a C# (.NET Framework 4.8) WinForms app hosting its UI in Microsoft Edge WebView2.

- **License:** [MIT](LICENSE) for everything in this repository. Third-party parts: PeerJS (`Launcher/ui/peerjs.min.js`,
  MIT, see `Launcher/ui/LICENSE-peerjs.txt`) and the Microsoft WebView2 SDK (NuGet `Microsoft.Web.WebView2`,
  redistributable DLLs in `release/`).
- **Builds:** every release is built from this repository by [GitHub Actions](.github/workflows/build.yml) (`node build.mjs`).
- **Code signing:** Windows releases are signed through SignPath — free code signing provided by
  [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).
  Signing policy: <https://son-durak.pages.dev/kod-imzalama.html>
- **Privacy:** <https://son-durak.pages.dev/gizlilik.html> — no accounts, no analytics; stats stay on your PC.

---

[Son Durak](https://son-durak.pages.dev) oyununun launcher'ı ve kurulumu (Türkçe / English).

- **Oyun:** launcher, oyunu `Gokhan-Abi` reposunun `Son Durak` klasöründen indirir ve yeni commit'lerde "Güncelleme var" der; sadece değişen dosyaları indirir (git SHA-1 ile doğrulanır).
- **Launcher'ın kendisi:** bu reponun `release/` klasöründeki derlenmiş launcher'dır. Launcher her açılışta bu dosyaları kendi dosyalarıyla karşılaştırır; fark varsa indirir, doğrular, kendini değiştirir ve yeniden başlar. Launcher'ı güncellemek = `node build.mjs` + `release/`'i bu repoya göndermek.
- **Görünüm:** Steam kütüphane sayfası gibi: büyük oyun görseli, OYNA / GÜNCELLE düğmesi, sürüm, oynama süresi ve son oynama, altta **Yenilikler**. Yenilikler commit mesajlarından değil, oyun reposundaki `Son Durak/news.json` dosyasından gelir: oyuncular için yazılmış notlar (sürüm, tarih, başlık, maddeler; TR + EN) ve "Oyun hakkında" metni. Yeni bir oyun sürümü çıkınca en üste bir not eklemek yeterli. Oynama süresi `%LOCALAPPDATA%\SonDurak\launcher.json`'da tutulur.
- **Arkadaşlar:** sağdaki panel. Herkesin 8 karakterlik bir arkadaşlık kodu var (ör. `K7Q2-9XMB`); kodla istek gönderilir, kabul edilince mesajlaşılır, odaya davet edilir, oyundaki arkadaşın odasına **Katıl** ile girilir. Sunucu yok: launcher'lar oyunun çok oyunculusu gibi PeerJS (WebRTC) ile birbirine doğrudan bağlanır, eş adı `sondurak-fr-<kod>`. Arkadaş listesi, sohbetler ve gönderilmeyi bekleyen mesajlar sayfanın kendi deposunda (WebView2 profili) durur; çevrimdışı arkadaşa yazılan mesaj ikiniz de çevrimiçi olunca iletilir. Her arkadaşlıkta bir kez rastgele bir gizli anahtar paylaşılır; her bağlantıda karşı taraf bu anahtarla doğrulanır (anahtar tekrar gönderilmez), yani kodu bilen biri arkadaşın gibi davranamaz.
- **Oyunla köprü:** oyun açıkken launcher kapanmaz (kapatma düğmesi küçültür). Oyun `index.html?lang=..&name=..&join=..` ile açılır; oyundan launcher'a `friends`, `invite:{code,room}`, `room:KOD`, `name:..`, `lang:..` mesajları gelir, launcher oyuna `{type:'friends'|'invite'|'toast'|'join'|'name'}` gönderir.
- **Discord durumu:** launcher açıkken ve oyun oynanırken Discord profilinde "Son Durak oynuyor" görünür: launcher'da / ana menüde / co-op lobisinde / hat + tur, tek oyunculu ya da co-op (kaç kişi), geçen süre ve sitenin bağlantısı. Launcher bilgisayardaki Discord uygulamasıyla yerel IPC kanalından (`\\.\pipe\discord-ipc-N`) konuşur (`Launcher/Discord.cs`, kütüphane yok); Discord kapalıysa 15 saniyede bir yeniden dener. Ayarlardan kapatılabilir (`discord` seçeneği). Discord uygulaması: Developer Portal'daki "Son Durak" (Application ID kodda).
- **Microsoft Store sürümü:** aynı launcher'ın MSIX paketi (`store-msix.mjs`). Oyunu yine kendisi indirir ve günceller (arkadaşlar, davetler, Discord durumu aynı); sadece kendini güncellemez, onu Store günceller (`Config.Store`: paketli çalışınca ya da `SONDURAK_STORE=1` ile açılır). Ayarlarda oyun klasörü satırı yoktur (Windows o klasörü pakete özel bir yere yönlendirir).
- **Dil:** üst çubuktaki TR / EN düğmeleri. Seçim `%LOCALAPPDATA%\SonDurak\launcher.json`'a kaydedilir ve oyunla eşitlenir (oyun `index.html?lang=..` ile açılır, oyunda değiştirilirse launcher'a bildirir). Kurulum penceresi de bu seçimi / Windows dilini kullanır.

## Klasörler

```
Launcher/   C# (.NET Framework 4.8) + WebView2: launcher penceresi, oyun penceresi, güncelleyici
Setup/      yönetici izni istemeyen kurulum (payload.zip gömülü)
release/    derlenmiş launcher: launcher'lar kendini buradan günceller (anahtar İÇERMEZ)
build.mjs   derleme: release/ + dist/SonDurak-Setup.exe (10 MB altı olmalı)
store/      Store paketinin logoları
store-msix.mjs  Store paketi: dist/SonDurak-Store-<sürüm>.msix (önce build.mjs)
```

## Derleme

```
node build.mjs
```

Gerekenler: .NET SDK, `SONDURAK_READ_TOKEN` ortam değişkeninde **sadece okuma izinli** (Contents: Read-only) bir GitHub anahtarı; `Gokhan-Abi` ve `Son-durak-Launcher` repolarına erişebilmeli. Derleme anahtarın iki repoyu da okuyabildiğini ve hiçbirine yazamadığını dener, sonra onu kurulum paketine ayrı bir `token.bin` olarak koyar (exe'nin içinde değildir; derleme bunu kontrol eder). `token.bin` ve `dist/` asla repoya gönderilmez. Anahtarın süresi dolarsa yeni anahtarla setup'ı yeniden derleyip kurmak gerekir.

GitHub Actions da aynısını yapar (`.github/workflows/build.yml`): anahtar reponun **Settings → Secrets and variables → Actions** kısmında `SONDURAK_READ_TOKEN` adlı gizli değişken olarak durur. İmzalanmamış çıktı "SonDurak-unsigned" adlı artifact olarak yüklenir; SignPath projesi açılınca aynı iş akışı onu SignPath'e imzalatır.

Store paketi: `node build.mjs` sonra `node store-msix.mjs 1.9.5.0` (Windows SDK'daki makeappx gerekir). Sürümün son sayısı 0 olmalı ve her Store gönderiminde büyümeli. Paket imzasızdır, Store imzalar; Partner Center'a yüklenir.

## Test bayrakları

- `SonDurakLauncher.exe --selftest-install <json>`: oyunu başsız indir / güncelle
- `SonDurakLauncher.exe --selftest-selfupdate <json>`: launcher'ı bu reponun `release/`'i ile güncelle (yeniden başlatmadan)
- `SonDurakLauncher.exe --shot-launcher <png>` / `--shot-game <png>`
- `SONDURAK_INSTANCE=<ad>`: açık bir launcher'ın yanında ikinci bir test kopyası çalıştır
- `SONDURAK_DATA_DIR=<klasör>`: ayrı bir profil (iki launcher'ı birbirine arkadaş ekletmek için), `SONDURAK_TEST_OFFSCREEN=1`: pencereyi ekran dışında aç, `SONDURAK_NO_SELFUPDATE=1`: kendini güncellemeyi kapat, `SONDURAK_STORE=1`: Store sürümü gibi davran
- `SonDurakLauncher.exe --selftest-discord <json> [saniye]`: Discord'a sırayla her durumu gönderir (launcher, menü, lobi, tek oyunculu, co-op, bitiş) ve Discord'un cevaplarını yazar; `SONDURAK_DISCORD_LOG=<dosya>`: gönderilen her durum bir satır; `SONDURAK_NO_DISCORD=1`: testlerde Discord durumunu kapatır
- `SonDurak-Setup.exe --silent --test --dir <klasör>` (dikkat: kurulum açık launcher'ları kapatır)
