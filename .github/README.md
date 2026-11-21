# ScreenCycle

Created by **Luka LAURENT**.

Turns a Windows 11 PC into a display screen: apps, documents, existing windows and websites take turns
on screen, each for the time you choose. Lives in the system tray.

## Start it

Download the repository (green **Code** button › **Download ZIP**), unzip it anywhere, and double-click
**`ScreenCycle.cmd`**. A small window opens where you can:

- launch ScreenCycle,
- add a shortcut on the desktop and in the Start menu,
- make it start automatically when you sign in to Windows.

If ScreenCycle is already running, the same window offers **Open settings** and **Stop ScreenCycle**.
After that, you can also start it from the shortcuts. No console window appears.

The first time, the Settings window opens. Add your screens, then click **Save & start rotation**.

ScreenCycle runs through Windows' own PowerShell (`app\ScreenCycle.ps1` contains the whole program), so it works
even on PCs where Smart App Control blocks unsigned `.exe` files.

## What a screen can be

- **Website**: opens in its own private browser window in kiosk mode (no tabs, no address bar).
  Edge, Chrome, Firefox or Brave are detected automatically. A login you make there is kept for next time.
  **Profile**: choose *My profile: …* to open it in your own browser profile instead, so you are already signed in
  (Google, YouTube…). It opens as a new window even while that browser is open, and ScreenCycle makes it fullscreen
  with F11. If ScreenCycle restarts, it takes the same window again instead of opening a second one.
- **Application or file**: any `.exe`, shortcut (`.lnk`), PDF, picture, PowerPoint (use argument `/S` for a slideshow)…
- **Already open window**: matched by part of its title and/or its process name ("Pick an open window…").

Each screen has its own duration, display mode (Fullscreen / Maximized / Leave as is), an optional
"refresh (F5) each time it comes on screen", and for apps that draw their own title bar, an option to hide it.

## Controls

Left-click the tray icon to open Settings; right-click it for Pause, Next, Previous, Show now…, Exit.

| Default shortcut | Action |
|---|---|
| `Ctrl + Alt + P` | Pause / resume |
| `Ctrl + Alt + →` / `←` | Next / previous screen |
| `Ctrl + Alt + S` | Open Settings (the rotation is stopped while it is open) |
| `Ctrl + Alt + Q` | Exit |

All shortcuts can be changed in Settings › Shortcuts. They work system-wide, even over fullscreen apps.

## Reliability features

- Smooth crossfade (or fade through black) between screens; a "Preparing" screen hides the launches at start.
- Fullscreen screens always cover the taskbar. If Windows refuses to give a screen the focus (for example
  because someone is typing in another app), ScreenCycle keeps it above the taskbar anyway and takes the focus
  back as soon as the PC is left alone.
- Optional: hide the Windows taskbar automatically while rotating (Settings › Behavior). Your normal taskbar
  setting comes back when the rotation stops, and also after a crash or power cut.
- Optional: pause when someone uses the mouse or keyboard, and resume after N seconds without activity.
- Reopens a screen whose window was closed or crashed; brings the rotation back if another window pops up over it.
- Keeps the display awake (no sleep / screen saver) while rotating.
- Optional start with Windows (with a delay so the desktop can settle), optional progress line, short on-screen messages.
- Optional mouse pointer parking: at the middle of the right edge, where it is nearly invisible and doesn't bring up the taskbar.
- On exit, gives "already open" windows their frame back. It can also close what it opened (off by default).
- Only one copy runs at a time; starting it again opens the Settings window.

## Command line

```
powershell -ExecutionPolicy Bypass -File app\ScreenCycle.ps1 [--welcome] [--settings] [--no-start] [--exit]
```

`--welcome` shows the launch window, `--exit` cleanly stops a running ScreenCycle (handy for maintenance scripts).

## Files

- Settings: `%LocalAppData%\ScreenCycle\config.xml`. **Portable mode**: a `ScreenCycle.config.xml` next to
  `app\ScreenCycle.ps1` is used instead, so you can copy a ready setup to another PC (Settings › About › Export).
- Log: `%LocalAppData%\ScreenCycle\ScreenCycle.log`
- Website browser profiles: `%LocalAppData%\ScreenCycle\profiles\`

## License

MIT — see [LICENSE](LICENSE).

## Build

```
powershell -ExecutionPolicy Bypass -File app\build.ps1
```

This uses the C# compiler built into Windows (.NET Framework 4.8), so no SDK is needed. Sources are in `app\src\`.
The build checks that everything compiles, then writes `app\ScreenCycle.ps1` (from `launcher.template.ps1` plus the
sources) and `ScreenCycle.cmd`.

## Known limits

- A few apps refuse to grow over the taskbar (for example the new Windows 11 Notepad). They show full size
  but the taskbar stays visible over them. The "hide the taskbar automatically" option helps there.
- Apps that draw their own title bar without telling Windows its size: set "App title bar › Cut off the top by N pixels".
