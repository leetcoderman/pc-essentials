# PC Essentials for Windows — Feature List

The macOS utility this started from has **87 features**. This is every one of them, mapped to Windows.

**Fit on Windows**
- ✅ Direct port: Windows has the APIs and nothing built in already does the same job.
- 🟡 Adapted: the idea carries over but has to be reshaped for Windows.
- ⚪ Built in or not needed: Windows already does it, or it only made sense on a Mac.
- 🔴 Hard: needs a driver, vendor-specific hardware access, or admin rights.

**Effort:** S = small, M = medium, L = large.

Proposed shell: a **system tray app** (in place of the Mac's menu bar icon) with a flyout panel, a Settings window and a **Features hub** where you install or uninstall individual features. Settings can be exported and imported. Suggested stack: C# / .NET 8 + WPF (or WinUI 3), using Win32 low-level hooks, Core Audio, WMI and Windows.Graphics.Capture.

---

## 1. Sound
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| S1 | Volume mixer | Per-app volume sliders in the tray panel, boost above 100%, pin and reorder favorites | ✅ (boost above 100% needs an audio processing hook, 🔴) | M |
| S2 | Per-app output | Send one app to the speakers and another to a headset | ✅ (undocumented `IAudioPolicyConfig`, the same API EarTrumpet uses) | M |
| S3 | Output switcher | Switch output device with a hotkey; lower the volume when headphones disconnect | ✅ | S |
| S4 | Audio priority | Ranked list of preferred output and input devices; switch automatically when one connects | ✅ | S |
| S5 | Mic mute | Global hotkey to mute the mic, with an on-screen indicator, plus a preferred input device and its level | ✅ | S |
| S6 | Music app blocker | Stop Music opening when you press the media keys | 🟡 (equivalent: stop media keys launching the Groove/Media Player app) | S |

## 2. System monitor
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| M1 | CPU | Usage, per-core usage, temperature and history graphs | ✅ (temperatures need LibreHardwareMonitor and admin rights) | M |
| M2 | GPU | Usage and VRAM | ✅ | S |
| M3 | Memory | Used memory, memory pressure and the heaviest apps | ✅ | S |
| M4 | Network | Live traffic, session totals, local and public IP, speed test | ✅ | M |
| M5 | Disk | Free space and read/write activity | ✅ | S |
| M6 | Power / battery | Battery health, charge cycles, power draw and time remaining | ✅ (laptops) | S |
| M7 | Connected devices | Battery levels of connected Bluetooth and USB devices | 🟡 | M |
| M8 | Fan control (beta) | Manual fan speeds or temperature curves | 🔴 (depends on the motherboard; via LibreHardwareMonitor) | L |
| M9 | Tray readouts | Live CPU, RAM, network or temperature numbers shown as tray icons | ✅ | S |
| M10 | Alerts | Notify on sustained high CPU, high temperature, memory pressure, low disk space or low battery | ✅ | S |

## 3. Windows and taskbar
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| W1 | App switcher | Alt+Tab replacement with live previews, search and per-monitor filters | ✅ (DWM thumbnails) | L |
| W2 | Window layout | Snap windows to halves, thirds, quarters or custom layouts with hotkeys, screen edges or a modifier+drag gesture; move windows between monitors; restore earlier positions | ✅ (overlaps with built-in Snap and FancyZones, so the value is in hotkeys and drag gestures) | M |
| W3 | Taskbar preview | Window previews on hover, with close, move and snap actions | 🟡 (Windows has basic previews; this would be an enhanced overlay) | L |
| W4 | Taskbar click actions | Click an active app's taskbar button to minimize it or cycle through its windows | 🟡 (minimizing is built in; cycling windows is new) | M |
| W5 | Maximize without fullscreen | | ⚪ Windows already maximizes this way | – |
| W6 | Fixed desktop order | | ⚪ Windows virtual desktops already keep a fixed order | – |
| W7 | Quit on close | Exit selected apps when their last window closes, instead of leaving them in the tray | ✅ | S |
| W8 | Quit / close protection | Guard against an accidental Alt+F4 or Ctrl+W with hold-to-confirm, double press or an extra modifier, per app | ✅ | S |

## 4. Keyboard and mouse
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| K1 | Text snippets | Typed triggers expand into text, with clipboard, date and time variables, plus a searchable snippet menu | ✅ | M |
| K2 | Smooth scrolling | Smooth, adjustable scrolling for the mouse wheel | ✅ | M |
| K3 | Pointer acceleration toggle | Turn off "Enhance pointer precision" and restore it when the feature is turned off | ✅ | S |
| K4 | Linear scrolling | Every wheel notch scrolls the same number of lines | ✅ | S |
| K5 | Focus follows mouse | Bring the window under the pointer forward after a delay | ✅ (Windows has a hidden setting for this; this version adds the delay and exclusions) | S |
| K6 | Scroll direction | Reverse mouse scrolling, vertical and horizontal separately (Windows only offers this for touchpads) | ✅ | S |
| K7 | Horizontal scroll while holding a key | Hold a chosen key to turn wheel movement into sideways scrolling | ✅ | S |
| K8 | Modifier + scroll zoom | Zoom with a modifier key and the wheel in apps that don't support it | ✅ | S |
| K9 | Side buttons | | ⚪ Back/Forward already work on Windows | – |
| K10 | Mouse button shortcuts | Map extra mouse buttons to keys; button+drag gestures to switch virtual desktops or open Task View | ✅ | M |
| K11 | Middle click | Three-finger touchpad tap as a middle click | ⚪ (Precision touchpad settings already offer this) | – |
| K12 | Excluded apps | Turn mouse enhancements off for chosen apps | ✅ | S |
| K13 | Extra click filter | Ignore double clicks caused by a worn mouse switch | ✅ | S |
| K14 | Key debounce | Ignore repeated letters from a worn keyboard | ✅ (Windows Filter Keys is cruder) | S |
| K15 | Super key | Caps Lock or a right-side modifier acts as Ctrl+Alt+Shift+Win (a "hyper" key), with a separate tap action and app exclusions | ✅ | M |
| K16 | Shortcut manager | All feature hotkeys in one place, with conflict detection and optional takeover of Windows shortcuts | ✅ | M |

## 5. Clipboard, files and links
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| C1 | Clipboard history | Searchable history of text, images and files, with pins, previews, paste hotkeys, excluded apps and a retention limit (much more than Win+V) | ✅ | M |
| C2 | Auto-clear clipboard | Clear the clipboard after a delay or when the PC locks or sleeps | ✅ | S |
| C3 | Paste as plain text | Hotkey to paste without formatting | ✅ | S |
| C4 | Shelf | Floating drop zone that appears while you drag, to park files, text and links | ✅ | M |
| C5 | Explorer extras | Paste a copied image as a PNG file in Explorer (cut/paste and F2 rename already exist) | 🟡 | S |
| C6 | Clean URL | Remove tracking parameters (utm, fbclid and similar) from copied links, manually or automatically | ✅ | S |
| C7 | Installer helper | Mount an ISO, run its installer, eject it and delete the download | 🟡 | S |

## 6. Everyday tools
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| T1 | Command bar | Launcher that searches apps, windows, files, clipboard and snippets, with a calculator, unit conversion, emoji search and saved scripts | ✅ (overlaps with PowerToys Run) | L |
| T2 | Quick panel | Floating palette of favorite tools on a hotkey | ✅ | S |
| T3 | Quick toggles | Dark/light mode, hide desktop icons, eject drives, empty the Recycle Bin, lock, hide taskbar and more | ✅ | S |
| T4 | Quit apps | List of open apps: close them one by one, all at once, or all except the one in front | ✅ | S |
| T5 | Radial menu | Wheel of apps, files and shortcuts around the pointer, with profiles and submenus | ✅ | M |
| T6 | Scratchpad | Autosaved notes in tabs, with Markdown preview and export | ✅ | M |
| T7 | Cleaning mode | Lock the keyboard (and optionally the mouse) while cleaning, with a black screen or a small indicator | ✅ | S |
| T8 | Kill process | Pick a window or search for a process and force-kill it | ✅ | S |
| T9 | Port manager | List processes listening on ports and kill them | ✅ | S |
| T10 | Wallpaper gallery | Browse built-in wallpapers and your own folders, and set them per monitor | ✅ | S |

## 7. Capture and create
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| X1 | Capture selector | One overlay to switch between screenshot, recording, text grab and color picker, with a pixel magnifier | ✅ | M |
| X2 | Screenshot | Capture an area, window, screen or scrolling page; annotate, crop, redact, add backgrounds and watermarks; pin to screen | ✅ (scrolling capture is hard) | L |
| X3 | Screen recording | Separate system-audio and mic tracks; trim, auto-zoom and blur; export MP4 or GIF | ✅ | L |
| X4 | Camera preview | Floating webcam mirror to check yourself before a call | ✅ | S |
| X5 | Copy text from screen | Offline text recognition (Windows.Media.Ocr) and QR code reading | ✅ | S |
| X6 | Color picker | Copy a screen color as HEX, RGB or HSL | ✅ | S |
| X7 | Media tools | Compress videos, batch-convert images, add watermarks, make GIFs (bundled ffmpeg) | ✅ | M |

## 8. App management
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| A1 | App updates | List outdated apps from `winget upgrade` and the Store, and update them together | ✅ | M |
| A2 | Cleaner | Remove temp files, caches, logs and leftovers, manually or on a schedule | ✅ | M |
| A3 | Messaging downloads | Retention rules for WhatsApp/Telegram download folders | ✅ | S |
| A4 | Uninstaller | Uninstall an app and review the files and registry keys it leaves behind | ✅ | M |
| A5 | Package manager UI | Search, install and remove winget (or Scoop/Chocolatey) packages without a terminal; the Mac version did this for Homebrew | ✅ | M |

## 9. Energy and display
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| E1 | Keep awake | Prevent sleep on a timer, while chosen apps run, on AC power or with an external display; optional mouse jiggle | ✅ | S |
| E2 | Displays / brightness | Per-monitor brightness, using DDC/CI hardware control on external monitors, finer steps, and software dimming below a monitor's minimum | ✅ | M |
| E3 | Extra brightness | Use HDR headroom to go past normal maximum brightness | 🔴 (not feasible on most hardware) | – |
| E4 | Bluetooth on sleep | Turn Bluetooth off on sleep and back on at wake, only if this app turned it off | ✅ | S |

## 10. Dynamic Island (top-center overlay)
Windows laptops have no camera notch, so this would be a **floating pill at the top center of the screen** that expands on hover.
| ID | Feature | What it does | Fit | Effort |
|---|---|---|---|---|
| D1 | Island core | Now-playing media with controls (GlobalSystemMediaTransportControls), volume and brightness indicators | ✅ | L |
| D2 | Calendar | Upcoming events | 🟡 (Outlook/ICS) | M |
| D3 | Notifications | Recent notifications shown in the island | 🟡 (UserNotificationListener) | M |
| D4 | Gestures | Swipe or scroll on the island to skip tracks or change volume | ✅ | S |
| D5 | Timer | Countdown timer and stopwatch | ✅ | S |
| D6 | Accessories | Battery levels of headphones and other devices | 🟡 | S |
| D7 | Lyrics | Synced lyrics for the current song (online lookup) | ✅ | M |
| D8 | Queue | Upcoming tracks | 🟡 (Spotify only) | M |
| D9 | Live equalizer | Animated audio bars | ✅ (WASAPI loopback) | S |
| D10 | Downloads | Download progress and a file tray | ✅ | S |
| D11 | AI agents | Usage, limits and token counts for Claude Code, Codex, OpenCode and Copilot, read from local logs in `%USERPROFILE%` | ✅ | M |
| D12 | Watch | Watch a screen area and alert when it changes | ✅ | S |
| D13 | Mascot | Animated companion | ✅ | S |

## 11. Shell and platform
| ID | Feature | What it does | Effort |
|---|---|---|---|
| P1 | Tray app and flyout panel | Tray icon; tabbed panel with Mixer / System / Controls / Utilities | M |
| P2 | Features hub | Install or uninstall each feature; uninstalled ones stop loading | M |
| P3 | Presets | Starter bundles, such as "Developer" or "Minimal" | S |
| P4 | Settings export / import | | S |
| P5 | Start with Windows | | S |
| P6 | Privacy | No telemetry; the network is used only for actions you can see | – |
| P7 | Localization | Many languages on the Mac (English first here) | M |

---

### Suggested first build (MVP)
P1, P2, P5, S1, S3, S5, M1–M6, M9, W2, W8, K1, K6, C1, C3, C6, T3, T4, E1, E2, X5, X6

**Tell me the IDs you want built** (or a group, e.g. "all of Sound + Clipboard").
