# PC Essentials

A PowerToys-style tray utility for Windows. See [FEATURES.md](FEATURES.md) for the full feature map.

Build with the .NET 10 SDK: `dotnet build src/PCEssentials/PCEssentials.csproj`. Run the tests with `dotnet test tests/PCEssentials.Tests`.

**What's in it:** a Dynamic Island on the taskbar (AI agent limits for Claude Team seats and Codex, now playing, volume mixer, calendar, clipboard history, scratchpad, microphone, system monitor, keep awake), live CPU/GPU/RAM/network readouts beside the clock, Rectangle-style window snapping on Win shortcuts, a Lightshot-style screenshot tool, a Spotlight-style search box on Alt+Space (apps, files, windows, settings, calculator, web), shake-to-find cursor, and a game mode that blocks the Windows key in games.

**License:** GPL-3.0-or-later (see [LICENSE](LICENSE) and [NOTICE](NOTICE)). CPU temperatures use [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and need administrator rights plus the PawnIO driver.

## Game mode: block the Windows key in games

While a game is in front, PC Essentials swallows Left Win and Right Win. The Start menu doesn't open, and Win shortcuts (Win+D, Win+Tab, and PC Essentials' own Win snapping shortcuts) don't fire. Win+<key> reaches the game as the plain key. As soon as another app is in front, the Windows key works normally again. Nothing needs to be switched by hand.

**What counts as a game.** The foreground app counts as a game when either of these is true:
1. Its exe is on the **Games** list (for example `cs2.exe`). Matching ignores case, and `cs2` or a full path also work.
2. **Auto-detect fullscreen games** is on (the default) and the app is fullscreen. Windows must report it as exclusive (D3D) or borderless fullscreen through `SHQueryUserNotificationState`, or the window must cover its whole monitor.

Apps on the **Never treat as games** list are never blocked, even fullscreen. That list wins over the Games list. By default it has the common browsers, VLC, MPC-HC, PotPlayer and PowerPoint, so fullscreen videos and slideshows keep the Windows key.

**Settings → Games**
- Turn game mode on or off. The tray menu also has a toggle: "Block Win key in games".
- Add games by exe name, from the running-apps picker, or with **Add last active app**.
- **Pause shortcut**: turns blocking off until you press it again. The default is `Ctrl+Alt+Shift+W`. Win can't be part of it, because Win is blocked in games. There's also a Pause/Resume button.
- **Notifications** are quiet by default. You can choose a red dot on the tray icon while blocking, or a notification when blocking turns on or off.

**How it works.** PC Essentials' single low-level keyboard hook (`WH_KEYBOARD_LL`) reads a cached "game is active" flag; nothing is injected into games. That flag is computed outside the hook by a `SetWinEventHook` foreground tracker plus a 1-second recheck, and decisions are logged to `%APPDATA%\PCEssentials\pc-essentials.log`. Key handling:
- When a Win press is swallowed, its release is swallowed too.
- A Win key that was already held when the game took focus is let through, so it can't get stuck.
- Injected (synthetic) key events are ignored.

The hook is installed only while game mode or a shortcut needs it, and it is installed again after the PC resumes from sleep.

**Limits**
- **Win+L** and **Ctrl+Alt+Del** can't be blocked; Windows handles them before any hook.
- **Elevated games.** If a game runs as administrator and PC Essentials doesn't, Windows (UIPI) hides that game's keystrokes from PC Essentials, so the Windows key can't be blocked in it. Settings → Games shows a warning naming the game, with a **Restart as administrator** button. PC Essentials never elevates itself.
- **Fullscreen detection is a heuristic.** Borderless "fullscreen" apps that aren't games count too. Add them to the exclude list. A game running in a window needs to be on the Games list.
