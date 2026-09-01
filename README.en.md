# BlackBossKey

> One hotkey to black out every screen — instantly. Your PC keeps running as if nothing happened.

A 100% local Windows screen-privacy tool. Press a hotkey and every monitor turns into a pure-black cover instantly, while the computer itself keeps running normally — no lock screen, no sleep, no shutdown, no interruption to any program.

Two modes are supported: **Boss Mode** (full blackout + mute + hidden cursor) and **Remote Stealth Mode** (physical screens black, but remote-desktop apps on your phone still see and control the real desktop).

🇨🇳 [中文文档](README.md)

📚 **Want the full technical breakdown?** See [docs/technical-details.md](docs/technical-details.md) — every Windows API used, how the code works, and the pitfalls encountered along the way.

## Screenshot

*(The program has no main window — only a system-tray icon. The black-out effect is pure RGB(0,0,0) black, so a screenshot would tell you nothing.)*

## Hotkeys

| Hotkey | Function |
|---|---|
| `Ctrl + Alt + F12` | **Boss Mode**: full pure-black cover + mute + hidden cursor |
| `Ctrl + Alt + F11` | **Remote Stealth Mode**: physical screens black, phone remote control still works |
| `Ctrl + Alt + Esc` | **Force Restore** (always brings your screens back, in any state) |

## Mode Comparison

| | Boss Mode (`F12`) | Remote Stealth Mode (`F11`) |
|---|---|---|
| Physical monitors | Pure black | Pure black |
| Remote desktop view | Also pure black | **Shows the real desktop** |
| Remote control | Unusable | Fully usable (click-through) |
| System audio | Muted (restored on exit) | Untouched (you can hear sound on your phone) |
| Mouse | Cursor hidden (covered by the black screen), but the mouse still moves and clicks normally; returns to its original position after restore | Visible (your phone can see the cursor) |
| Status light | Status dot: red = blackout on (persistent), green = off (fades out after 5 s), visible both physically and in the remote view | Same |
| Running programs | All unaffected | All unaffected |

## Features

### Core
- **Multi-monitor support**: auto-detects the position and size of every display (`Screen.AllScreens`), one pure-black cover per screen — mixed resolutions, mixed scaling, any arrangement all supported
- **Truly pure black**: RGB(0,0,0) — no text, no logo, no taskbar, no UI elements of any kind
- **No lock screen**: never calls Win+L, never signs out, never sleeps, never cuts display power — just a cover layer on top of your screens
- **Programs unaffected**: videos, downloads, games, builds, AI agents — everything keeps running normally

### Boss Mode (`Ctrl+Alt+F12`)
- Mutes the default system audio output device (previous state is remembered and restored)
- Hides the mouse cursor; it returns to its pre-blackout position after restore

### Remote Stealth Mode (`Ctrl+Alt+F11`)
- Uses Windows `WDA_EXCLUDEFROMCAPTURE` (Win10 2004+) to make the cover window "invisible" to screen capture
- Chrome Remote Desktop, ToDesk, SunLogin, RustDesk, OBS and other remote/capture tools all see the real desktop
- Click-through (`WS_EX_LAYERED | WS_EX_TRANSPARENT`) — clicks and typing from your phone reach the real windows underneath
- Status dot in the top-right corner (not excluded from capture, so it's visible on your phone)
- The capture-exclusion flag is re-applied unconditionally every 250 ms, in case a DWM restart silently drops it

### Safety & Stability
- **Atomic initialization**: if any step of the blackout setup fails, everything rolls back automatically — the screen is guaranteed to be recoverable
- **Unconditional force cleanup**: `Ctrl+Alt+Esc` doesn't rely on any state flag; it wipes all side effects at any time
- **Crash recovery**: process force-killed mid-mute? Next launch detects the leftover mute and restores it automatically
- **System-level topmost error dialog**: even errors are shown above the blackout cover — always visible, always closable
- **Emergency cursor restore**: `BlackBossKey.exe --restore-cursors` restores system cursors in an emergency
- **Single-instance protection**: launching twice won't create a second instance
- **Keyboard guard**: while blacked out, the Win key, Ctrl+Esc and Alt+Tab are intercepted — no Start Menu, no task switcher
- **Mouse stays usable**: the black cover sits above the mouse (cursor invisible), but movement and clicks work normally, hitting the programs underneath
- **Status light**: small dot in the top-right corner of each screen — turns red when the blackout is on (persistent, visible in the remote view), green when off (fades out after 5 s so it doesn't linger on your desktop)
- **Dock widget compatibility**: automatically hides topmost desktop widgets (e.g. DesktopDock) during blackout and restores them afterwards, keeping the blackout unbroken
- **Monitor hot-plug**: connect/disconnect monitors during blackout — coverage is rebuilt automatically

### Privacy
- **Fully local**: no network connection, no data upload, no keylogging, no screenshots, no telemetry, collects nothing

## Usage

### Run directly (recommended)

1. Download `BlackBossKey.exe` from [Releases](../../releases) (self-contained single file, no .NET install required)
2. Double-click to run; the program lives in the system tray
3. Use the hotkeys, or right-click the tray icon for the menu

### Start with Windows

Drop a shortcut to `BlackBossKey.exe` into the Startup folder (`Win+R` → `shell:startup` → Enter).

## Building from Source

Requires [.NET SDK 8.0](https://dotnet.microsoft.com/download) or later.

```bash
git clone https://github.com/milkloon-777/BlackBossKey.git
cd BlackBossKey
dotnet publish BlackBossKey.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The artifact is at `bin\Release\net10.0-windows\win-x64\publish\BlackBossKey.exe` (~107 MB, self-contained single file).

## Tech Stack

| Technology | Purpose |
|---|---|
| C# / .NET 10 | Language & runtime |
| Windows Forms | Windows & system tray |
| `RegisterHotKey` | Global hotkey registration |
| `Screen.AllScreens` | Multi-monitor layout detection |
| `SetWindowDisplayAffinity` (`WDA_EXCLUDEFROMCAPTURE`) | Screen-capture exclusion |
| `WS_EX_LAYERED \| WS_EX_TRANSPARENT` | Click-through |
| WASAPI / Core Audio (`IAudioEndpointVolume`) | Audio muting |
| `WH_KEYBOARD_LL` | Low-level keyboard hook (Win key interception) |
| `SetWindowPos` (`HWND_TOPMOST`) | Lossless topmost re-assertion |
| `ShowCursor` / `GetCursorPos` / `SetCursorPos` | Mouse control |

## Project Structure

```
BlackBossKey/
├── BlackBossKey.csproj    # Project file
├── Program.cs             # All source code (single file, commented)
├── .gitignore
├── LICENSE                # MIT
└── README.md              # The one you're reading
```

## System Requirements

- Windows 10 2004 (Build 19041) or later / Windows 11
- x64 architecture
- Remote Stealth Mode requires Windows 10 2004+ for `WDA_EXCLUDEFROMCAPTURE`

## Known Limitations

- `Ctrl+Alt+Del` and `Win+L` are Windows secure sequences — no user-mode program can intercept them
- Under DirectX exclusive fullscreen (some games), the cover may be pushed aside
- If your remote software's built-in "privacy screen" feature is enabled, it conflicts with this tool — pick one
- On rare GPU/driver combinations, some capture paths may still see the cover (a known edge of `WDA_EXCLUDEFROMCAPTURE`)

## Development History

This project went through many iterations, each fixing a real-world problem:

| Version | Key Changes |
|---|---|
| V1 | Initial release: Boss Mode + multi-monitor + audio mute + hidden mouse |
| V2 | Startup balloon notification + logging + hotkey conflict fallback |
| V3 | Remote Stealth Mode (`WDA_EXCLUDEFROMCAPTURE`) |
| V4 | System-level mouse hiding + bottom-right flicker fix |
| V5 | Keyboard hook (Win key interception) — contained a severe bug, reverted |
| V6 | Atomic initialization + unconditional force cleanup + topmost error dialog (incident fix) |
| V6.1 | Remote mode: mouse hiding reverted (phone needs to see the cursor) |
| V6.2 | Click-through fix (standard `LAYERED + TRANSPARENT` combination) |
| V6.3 | Status indicator for Remote Stealth Mode (red dot) |
| V6.4 | Crash recovery (audio auto-restored after force-kill) |
| V6.5 | DesktopDock widget compatibility |
| V6.6 | Boss Mode: cursor hidden but mouse still usable |
| V6.7 | Persistent status light: red = on, green = off |
| V6.8 | Green light fades out after 5 s |

## License

[MIT License](LICENSE)

## Acknowledgements

- [LZong-tw/turn-off-screen](https://github.com/LZong-tw/turn-off-screen) — inspiration for `WDA_EXCLUDEFROMCAPTURE` and re-applying the flag after DWM restarts
- [Microsoft Win32 API docs](https://learn.microsoft.com/windows/win32/api/) — `SetWindowDisplayAffinity`, `SetWindowPos`, Core Audio API
