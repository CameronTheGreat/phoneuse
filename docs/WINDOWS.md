# Phone Use on Windows

Windows can't act as a Bluetooth Classic keyboard, so on Windows Phone Use is a
Bluetooth LE keyboard and mouse instead (HID over GATT, through the Windows
Runtime). It uses the PC's own Bluetooth adapter, needs no driver and no admin
rights, and Windows keeps using the adapter for everything else. The mirror window is
a native WinUI 3 app (`windows/PhoneUseMirror/`), or the browser mirror in an Edge or Chrome window
when that app isn't built.

| | Tested with |
|---|---|
| Windows | Windows 11 |
| iPhone | iPhone 17 Pro Max and iPhone 14 Pro Max, iOS 26 |
| Python | 3.13, from python.org |
| UxPlay | 1.74, built in MSYS2 by the installer |
| .NET SDK 10 | builds the Phone Use window (without it: Edge or Chrome) |
| NVIDIA GPU (optional) | NVENC for the video hub, CUDA for the models that read the screen |

1. **Install**, from the checkout, in PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File windows\scripts\install.ps1
   ```
   The installer makes `.venv`, installs MSYS2 to `C:\msys64` and builds UxPlay there,
   builds the Phone Use window into `windows\dist` when the .NET SDK is installed
   (`windows\scripts\build-winui.ps1` builds it again), adds a Windows Firewall rule
   for UxPlay (it asks for admin rights once; the rule allows the local network only),
   puts `bin` on your PATH and downloads the models that read the screen (`-NoVision`
   skips them). It ends with `phoneuse doctor`, which checks each part and says how to
   fix what is missing.
2. **Start Phone Use:** open `windows\dist\PhoneUseMirror.exe`, which starts phoneuse in
   the background when it isn't running, or run `phoneuse up`, which opens the window.
   Then, on the iPhone, open Control Center, tap Screen Mirroring and choose
   **Phone Use on <your computer>**.
3. **Pair**, once: on the iPhone, open Settings › Bluetooth and tap this PC's Windows
   name, such as `DESKTOP-…` (Windows doesn't let Phone Use choose its Bluetooth LE
   name). Confirm on the phone, and on Windows if it asks.
4. **Turn on the pointer** as on Linux ([SOURCE.md](SOURCE.md), step 5). The first time, Phone Use measures the phone's
   pointer: over Bluetooth LE every phone needs that measurement.
5. **For agents, pair the iPhone app** ([COMPANION.md](COMPANION.md)): in the Phone Use
   window, Settings › Pair iPhone shows a QR code to scan with the Phone Use app.
   Agents are refused until an app is paired.

## The Phone Use window

The window is a client of the browser mirror's server (`phoneuse/web.py`): the same
video, input and settings, with lower latency (Media Foundation's hardware H.264
decoder straight to a Direct3D swap chain).

- **Input:** click to tap, drag to swipe, scroll with the wheel; a press on the bezel
  touches the nearest screen edge. Ctrl works as the phone's Cmd (the Windows key
  belongs to the Start menu) and the Windows key as its Ctrl; F1 Siri, F2 Home,
  F3 Search, F4 the on-screen keyboard.
- **The tray:** closing the window hides it to the notification area, so agents keep
  the phone. The tray icon opens it again; its menu shows phoneuse's log and quits.
  Quit stops the phoneuse the window started (not a `phoneuse up` in a terminal); the
  Bluetooth keyboard and mouse keep running, as after `phoneuse up`.
- **Settings** (the gear): keep the screen awake, start with Windows (phoneuse starts
  at sign-in with the window in the tray), the AssistiveTouch reminder, agent control,
  the iPhone app's pairing, screen reading (install or remove the models), and the
  passcode. The Bluetooth bridge's settings are in the browser mirror.
- **About** (the ⓘ button): how to use it and these differences from Linux.

The window's own settings are in `%LOCALAPPDATA%\phoneuse\PhoneUseMirror.json`.
`window = "browser"` in `config.toml` uses Edge or Chrome even when the window is built.

## How it differs from Linux

- The Bluetooth keyboard and mouse run as a background service that keeps running
  when `phoneuse up` stops, and the next `phoneuse up` uses it again. Each time the
  service starts, the iPhone sees its Bluetooth services disappear and come back, and
  one iPhone stopped using them until it was paired again. `phoneuse down` stops it.
- Pair only the phone that Phone Use should control. Over Bluetooth LE, iOS may give
  its name as just "iPhone", so when two paired phones are connected Phone Use can't
  tell which one is mirroring, and it sends nothing.
- A motherboard's Wi-Fi and Bluetooth card needs its antennas, even if the PC uses
  Ethernet. Without them the phone was heard at about -90 dBm next to the PC, the
  link dropped several times an hour, and keys and taps sent during a drop were lost.
- The phone takes a report every 15 ms over Bluetooth LE instead of every 8 ms, and
  the mirror trails the phone by about 0.1 s, so by hand it feels slower than on
  Linux. Agents don't notice.
- The PC isn't published over Bonjour (no Avahi), so the iPhone app finds it only at
  the address in its pairing link: pair again after the PC's address changes.
- The bridge hasn't been tried on Windows.

Logs are in `%LOCALAPPDATA%\phoneuse\run` (`phoneuse-up.log` when the window started
phoneuse). Agents use `bin\phoneuse.exe` (on your PATH after the install), guided by
[skill/SKILL.md](../skill/SKILL.md).

Configuration is the same as on Linux, in `%APPDATA%\phoneuse\config.toml` ([SOURCE.md](SOURCE.md#configuration)).
