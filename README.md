# StreamDeck Mobile Controller

Turn an Android phone into a full-screen, glowing Stream Deck for a Windows 10/11 PC.

* **Phone (Android, Flutter):** shows *only the buttons*, full screen. Swipe for pages. No editing on the phone.
* **PC (Windows, C# / .NET 8):** runs in the tray, executes every action, and hosts the **button editor** (opens in your browser).
  The layout lives on the PC and is pushed to the phone live.
* **Updates:** the host updates itself from GitHub Releases. The phone's deck UI is served by the PC, so it updates with the host —
  **no APK reinstall** for UI/layout/action changes.

```
mobile/                 Android app: pairing + full-screen WebView + connection settings
windows/StreamDeckHost/ Windows host: tray app, WebSocket server, editor (Web/editor.html), phone UI (Web/deck.html)
installer/              Inno Setup script (per-user installer)
tools/test-client.mjs   End-to-end protocol test (acts as a phone)
docs/PROTOCOL.md        Wire protocol and action reference
.github/workflows/      CI: builds APK + Windows exe/installer, publishes a Release on tags
```

## Using it
1. Start **StreamDeck Host** on the PC. On first run it offers to allow phone connections through Windows Firewall
   (one admin prompt). If you skipped it: Pairing tab → **Fix firewall**.
2. Install the APK, open it, **Scan QR code** shown on the host's *Pairing* tab.
3. Customize on the PC: host → **Open button editor** (or http://localhost:47800/editor on the PC). Drag to rearrange,
   click a button to change label, icon (6000+ built-in, emoji, your own image or an app's real icon), colors, glow, action,
   macro steps, confirmation. Changes autosave and appear on the phone immediately.

### Connection
* **Wi-Fi:** same network as the PC. The phone remembers addresses and re-discovers the PC by UDP broadcast if its IP changes.
* **USB:** USB debugging on, cable in; the host runs `adb reverse` automatically (adb must be on PATH or in the Android SDK).
* The deck reconnects automatically. If it can't connect, the phone shows the reason and a Try again / Pair again button.

**If it won't connect:** (1) host running? (tray icon) (2) host Pairing tab says *Firewall: phone connections allowed*? If not, press **Fix firewall**.
(3) phone and PC on the same Wi-Fi, not a guest/AP-isolated network. (4) VPN off on either side.

## Updates
* **Host:** on every start (and every 6 h) it checks the latest GitHub Release of `Devinhansaja/stream-desk`. If newer, it downloads
  `StreamDeckHost.exe`, verifies the SHA-256 from `StreamDeckHost.exe.sha256`, swaps itself in and restarts. Turn off in host *Settings*.
  Works when the exe is in a user-writable folder (portable or the per-user installer); in Program Files it only shows a notification.
* **Phone:** `deck.html` comes from the PC, so deck UI changes arrive with the host update. Only changes to the native shell
  (pairing screen, WebView, permissions) need a new APK.
* **Publishing an update (you):** bump `<Version>` in `windows/StreamDeckHost/StreamDeckHost.csproj`, commit, then
  `git tag v1.2.0 && git push origin v1.2.0`. GitHub Actions builds and attaches `StreamDeckHost.exe`, its `.sha256`, the installer and the APK.
  The host trusts whatever is in your repo's latest release, so keep the repo's access secure.

## Build
```powershell
./build-windows.ps1     # needs .NET 8 SDK → dist\StreamDeckHost.exe(+.sha256), portable zip, installer if Inno Setup is installed
./build-android.ps1     # needs Flutter 3.27+, Android SDK, JDK 17 → dist\StreamDeck-Mobile.apk
```
APK is debug-signed (fine for sideloading). Without local tools, push a tag and let CI build.

## Security
* QR contains a random 128-bit secret: single use, expires in 10 minutes. Pairing yields a 256-bit token; the PC stores only its hash.
  Revoke phones in host → Devices.
* Unauthenticated sockets can do nothing; 5 bad attempts lock an IP for a minute.
* The editor and its API answer only to `localhost` on the PC (loopback + Host check + custom header + same-origin), not to the LAN.
* Raw shell commands are **off** by default (host → Settings).
* **Limit:** traffic is plain `ws://` / `http://` on your LAN (no TLS). Use trusted networks or USB.

## Not verified on real hardware
Everything below the PC is tested (29 protocol checks incl. layout sync and editor access rules; auto-update swap tested with a
fake newer build). **The APK has not been run on a phone by me**: QR scan, WebView rendering, USB tunnel, haptics, brightness on your
display, mic mute and the installer are untested.
