# StreamDeck Mobile Controller

An Android app (Flutter) that turns your phone into a customizable Stream Deck for a Windows 10/11 PC,
plus a Windows companion (C# / .NET 8) that actually executes every action on the PC.

```
mobile/                 Flutter Android app (UI, button editor, macros, profiles, connection)
windows/StreamDeckHost/ Windows companion: tray app + dashboard + WebSocket server
installer/              Inno Setup script for the Windows installer
tools/test-client.mjs   End-to-end protocol test (acts as a phone)
docs/PROTOCOL.md        Wire protocol and action reference
.github/workflows/      CI that builds the APK and the Windows installer
```

## Status (honest)

| Part | State |
|---|---|
| Windows host | Built and tested on Windows 11: pairing, auth, volume, mute, macros, screenshots, app scan (177 apps found on the dev PC), single-file exe smoke-tested. 18/18 protocol tests pass (`tools/test-client.mjs`). |
| Android app | Complete source. Passes `dart analyze` (Flutter 3.27.4, one deprecation notice) and the 3 unit tests; manifest patching verified. **Never run on a phone/emulator and no APK built yet** (no Android SDK here), so UI behavior is unverified. |
| Installer | Inno Setup script written but **not yet built** (Inno Setup not installed here). The portable zip is built. |
| APK | Not built yet. Run `build-android.ps1` or push to GitHub (CI builds it). |

Not verified on real hardware: brightness (needs a laptop panel or a DDC/CI monitor), microphone mute, USB tunnel, QR scanning, discovery across a real router.

## Build

### Windows host (needs .NET 8 SDK)
```powershell
./build-windows.ps1
```
Produces `dist/StreamDeckHost-portable-win-x64.zip` and, if Inno Setup 6 is installed (`winget install JRSoftware.InnoSetup`),
`dist/StreamDeckHost-Setup-1.0.0.exe`. The installer adds a private-network firewall rule for TCP 47800 / UDP 47801 and an optional
"start with Windows" entry.

### Android app (needs Flutter 3.27+, Android SDK, JDK 17)
```powershell
./build-android.ps1
```
This runs `flutter create` once to generate the `android/` folder, adds the INTERNET/CAMERA permissions and cleartext-traffic flag
to the manifest, then builds `dist/StreamDeck-Mobile.apk`. Use `-SetupOnly` to just prepare the project for Android Studio / `flutter run`.
The APK is signed with the debug key (fine for sideloading; configure a keystore for Play Store releases).

### No local toolchain? Use CI
Push this folder to GitHub: `.github/workflows/build.yml` builds the APK and the installer and uploads both as artifacts.

## Using it

1. Install and start **StreamDeck Host** on the PC (tray icon appears). Allow it on private networks.
2. Install the APK on the phone. Open it → **Pair** → scan the QR code shown in the host's *Pairing* tab
   (or *Enter manually* with the IP and the code shown there).
3. Tap buttons. Tap **Edit** to change layout: tap a key to edit it, tap an empty slot to add one, long-press and drag to rearrange.

### Connections
* **Wi-Fi**: phone and PC on the same network. The phone finds the PC by UDP broadcast (port 47801) and remembers its address.
* **USB**: plug the phone in with USB debugging on. The host runs `adb reverse tcp:47800 tcp:47800` automatically
  (needs `adb` on PATH or in the Android SDK; toggle in Settings). The app tries `127.0.0.1` first in *Auto* mode.
* **Reconnect**: automatic, with backoff; also retried when the app returns to the foreground.
* **Status sync**: the host pushes volume / mute / mic / brightness changes about once a second.

### What buttons can do
Volume up/down/set, mute, microphone mute, media keys, brightness up/down/set, keyboard shortcuts (`ctrl+shift+esc`, `win+d`…),
typing text, opening installed apps / Steam games / Store apps / paths / URLs, closing a process, lock / sleep / sign out /
restart / shut down, screenshots (saved to `Pictures\StreamDeck Screenshots`), wallpaper (next / random / specific / folder,
with upload from the phone), macros (ordered lists of any of these with delays), and optionally raw shell commands.

Buttons are data: label, icon (built-in, emoji/text, or your own image; apps use their real icon), two gradient colors, glow,
confirmation prompt, and an action `{type, params}`. Nothing is hard-coded: the first-run layout is just a default profile you can edit or delete.

## Security model
* Pairing: the QR contains a random 128-bit secret, valid for 10 minutes and **single use**. Pairing yields a 256-bit device token;
  the PC stores only its SHA-256 hash. Revoke devices in the host's *Devices* tab.
* Unauthenticated connections can do nothing and are dropped after 15 s; 5 bad attempts from an IP lock it out for a minute.
* Raw shell commands are **off** by default (Settings in the host).
* **Limitation:** traffic is plain `ws://` on your LAN (no TLS), so use it on networks you trust, or use USB. Anyone on the same
  network who can sniff traffic could capture the token.
* Input injection cannot reach elevated (admin) windows, and Windows may ask for permission on first firewall use.

## Known limits
* Brightness works on laptop panels (WMI) and DDC/CI monitors; many desktop monitors don't expose it. The app shows "n/a".
* `sleep` may hibernate if hibernation is enabled (Windows behavior).
* Phone-side profiles are stored on the phone only; there is no cloud sync or import/export yet.
* Android only; no iOS by design.
