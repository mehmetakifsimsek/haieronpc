# HaierOnPC (haireonpc) ❄️🔥

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue.svg)](https://microsoft.com/windows)
[![Architecture](https://img.shields.io/badge/Architecture-C%23%20.NET%20Framework%204.x-68217a.svg)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Release](https://img.shields.io/badge/Release-Single--File%20Portable-success.svg)](#)

> **[TR]** Haier Europe **hOn** (`com.haiereurope.hon`) IoT ekosistemi için geliştirilmiş, tersine mühendislik (REA) tabanlı, yüksek performanslı, bağımsız (standalone) ve taşınabilir (portable) Windows masaüstü klima kontrol yazılımı.
>
> **[EN]** Standalone, high-performance, single-file portable Windows desktop client for Haier Europe **hOn** (`com.haiereurope.hon`) smart air conditioners, built via reverse-engineering (Salesforce OAuth2 + AWS Cognito + IoT Shadow telemetry).

---

## 🇹🇷 Türkçe Dokümantasyon

### 🌟 Öne Çıkan Özellikler

- **Tek Dosya Taşınabilir (Single-File Portable):** Kurulum, Node.js, Python veya harici runtime gerektirmez. Windows 10 ve 11 ile yerleşik gelen `.NET Framework` üzerinde tek bir `HaierHonDesktop.exe` (~210 KB) olarak çalışır. Arayüz (`ui/index.html`) doğrudan `.exe` içine gömülüdür.
- **Çift Arayüz Mimarisi:**
  1. **Hızlı Kontrol Merkezi (Glass Widget):** 60 FPS donanım hızlandırmalı, aerodinamik animasyonlu dairesel termostat kadranı, canlı durum göstergesi, tek tıkla hızlı modlar (22°C Soğutma, 25°C Isıtma, Sessiz Mod, Turbo Mod) ve cihaz seçim kutusu.
  2. **Detaylı Masaüstü Stüdyosu:** Geniş ekranlar için tasarlanmış profesyonel kontrol paneli:
     - 6 kademeli dikey salınım (Açı 1..5 ve 1-5 Sürekli Salınım)
     - Yatay salınım, fan hızı (Sessiz, Düşük, Orta, Yüksek, Turbo, Otomatik)
     - Akıllı modlar: Eko, Sağlık (UVC), Kendi Kendini Temizleme (56°C Sterilizasyon), 10°C Donma Koruması, Ekran Işığı, Uyku Modu
     - Sayısal tıklanabilir hedef sıcaklık ayarı (moda göre dinamik alt/üst limit kısıtlaması)
     - Canlı iç ve dış ortam sıcaklık/nem telemetrisi
     - Sol navigasyon dock'unda otomatik kategori takipçisi (Scroll-Spy)
- **Tarayıcı Bağımsızlığı:** Detaylı masaüstü arayüzü bilgisayarınızda hangi tarayıcı varsayılansa (**Chrome, Edge, Opera, Opera GX, Brave, Vivaldi, Firefox, Yandex vb.**) doğrudan o tarayıcı üzerinde uygulama modunda açılır.
- **Otomatik Model ve Özellik Tanıma:** Giriş yaptığınızda hesabınızdaki tüm klimalar (`Alt Kat`, `Üst Kat`, `Salon`, vb.) otomatik olarak bulunur. Klimanızın modeline (Revive, Flexis, Tundra, Pearl vb.) göre desteklenen ve desteklenmeyen özellikler (`capabilities`) otomatik ayırt edilir.
- **Demo / Çevrimdışı Önizleme:** Giriş yapmadan önce uygulamanın tüm görsel kabiliyetlerini test edebileceğiniz yerel sanal klimalar ile başlar.
- **Güvenli ve Yerel:** Şifreniz veya oturum bilgileriniz hiçbir harici sunucuya iletilmez; doğrudan Haier resmi bulutuna (`api-iot.he.services`) TLS 1.2 üzerinden bağlanır.

---

### 🚀 Başlangıç & Kullanım

1. **İndirin & Çalıştırın:**
   - [`HaierHonDesktop.exe`](./HaierHonDesktop.exe) dosyasını indirin ve çift tıklayın.
2. **Klima Seçimi & Hızlı Kontrol:**
   - İlk açılan hızlı kontrol penceresindeki **"Bağlı Klima Seçimi"** kutusundan klimanızı seçip tek tıkla açabilir, kapatabilir, derecesini ayarlayabilir veya modlar arasında geçiş yapabilirsiniz.
3. **Hesabınızı Bağlayın:**
   - Sol menünün altındaki kullanıcı simgesine veya ilk ekrandaki **"Hesap Bağla"** butonuna tıklayarak Haier hOn e-posta ve şifrenizle giriş yapın.
4. **Detaylı Paneli Açın:**
   - **"Detaylı Masaüstü Kontrol Panelini Aç"** butonuna basarak genişletilmiş yönetim stüdyosuna geçiş yapın.

---

### 🛠️ Kaynak Koddan Derleme

Projeyi Windows üzerinde derlemek için ek bir IDE kurmanıza dahi gerek yoktur. Windows ile birlikte gelen C# derleyicisi (`csc.exe`) yeterlidir:

```powershell
# PowerShell ile tek komutla derleme:
.\Build-PortableExe.ps1
```

---

## 🇬🇧 English Documentation

### 🌟 Key Highlights

- **Single-File Portable Executable:** Zero installation, zero external dependencies. No Node.js or Python required. Runs out-of-the-box on Windows 10 and 11 via native `.NET Framework 4.x` as a single `HaierHonDesktop.exe` (~210 KB) with HTML5/CSS3 assets embedded inside.
- **Dual UI Architecture:**
  1. **Control Center (Floating Glass Widget):** Smooth 60 FPS animated circular thermostat dial, real-time status telemetry, one-click quick presets (22°C Cool, 25°C Heat, Quiet, Turbo), and connected unit dropdown listbox.
  2. **Detailed Climate Studio:** Expansive desktop control dashboard:
     - 6-stage vertical swing control (Positions 1 to 5 + 1-to-5 Auto Continuous oscillation)
     - Horizontal swing & multi-speed fan control (Quiet, Low, Mid, High, Turbo, Auto)
     - Smart functions: Eco mode, Health (UVC), Self-Clean / 56°C Sterilization, 10°C Frost Prevention, Screen display toggle, Sleep mode
     - Numeric clickable temperature target setter with active-mode min/max safety clamping
     - Indoor / outdoor temperature and humidity sensors
     - Smart category navigation rail with auto Scroll-Spy
- **Browser-Agnostic Launch:** Uses whatever browser is set as default on your machine (**Google Chrome, Microsoft Edge, Opera, Opera GX, Brave, Vivaldi, Mozilla Firefox, Yandex, etc.**) in app window mode.
- **Dynamic Cloud Model & Capability Discovery:** Automatically queries your connected units, models (Revive, Flexis, Tundra, Pearl, etc.), and matches capabilities to render active vs. unsupported feature badges accordingly.
- **Offline / Demo Mode:** Start instantly in demo mode to preview all UI features before entering credentials.
- **Secure & Direct:** No third-party servers. All cloud traffic communicates directly with official Haier Europe IoT endpoints (`api-iot.he.services`) over TLS 1.2.

---

### 🚀 Getting Started

1. **Download & Launch:**
   - Download [`HaierHonDesktop.exe`](./HaierHonDesktop.exe) and run it anywhere on your Windows PC.
2. **Select Unit & Quick Controls:**
   - Use the **"Select Connected AC"** dropdown in the initial widget to switch units and control power, temperature, or presets.
3. **Connect Your hOn Account:**
   - Click the user avatar icon at the bottom of the navigation rail or the **"Sign In"** button on the widget, then input your official Haier hOn credentials.
4. **Open Full Climate Studio:**
   - Click **"Open Detailed Desktop Control Panel"** to access the complete multi-feature dashboard.

---

### 🛠️ Building from Source

No heavy IDE required. You can build the entire single-file portable binary with Windows' native C# compiler:

```powershell
# Run the automated build script in PowerShell:
.\Build-PortableExe.ps1
```

---

## 🔒 Security & Privacy / Güvenlik ve Gizlilik

- **Zero Telemetry Collection:** This application does not collect, track, or phone home any personal telemetry.
- **Local Credential Storage:** Tokens are securely stored in your local Windows user profile directory (`%APPDATA%\HaierHonDesktop\session.json`).
- **Direct Cloud Communication:** All authentication uses official Salesforce OAuth2 and AWS Cognito APIs used by the official hOn Android and iOS applications.

---

## 📄 License

MIT License. See [LICENSE](LICENSE) for details.
Disclaimer: This project is an independent community development and is not affiliated with, endorsed by, or sponsored by Haier Group or Haier Europe.
