# Home Assistant Desktop

A native WinUI 3 shell for a Home Assistant dashboard, built to run all the time in its
own process, with no connection to any other browser.

## Why this exists

Running the dashboard as an installed Chrome app puts it inside Chrome. It shares
Chrome's renderer scheduling, its GPU process, its memory pressure and its fate: every
other tab competes with the dashboard, and restarting Chrome restarts the dashboard.

This app hosts the same page in **WebView2 pointed at a private user-data folder**. That
single choice is what buys the isolation — WebView2 forks a complete browser instance
per profile, so the app gets its own browser process, GPU process, network service,
storage and renderers. Verified on this machine:

```
HomeAssistant.Desktop.exe (65504)
└── msedgewebview2.exe (33896)          --user-data-dir=...\HomeAssistantDesktop\WebView2
    ├── crashpad-handler
    ├── gpu-process
    ├── utility (network service, storage)
    └── renderer
```

Chrome's 25 processes were untouched throughout.

### Background throttling

Isolation alone is not enough. Chromium assumes it is a foreground browser and clamps
timers in backgrounded renderers, which is exactly the wrong behaviour for a dashboard
that lives behind other windows. The WebView2 environment is therefore created with:

```
--disable-background-timer-throttling
--disable-renderer-backgrounding
--disable-backgrounding-occluded-windows
--autoplay-policy=no-user-gesture-required
```

The last one lets camera cards start streaming without a click.

> **Related, on the server side:** this instance also runs the `ws_state_batch` custom
> component, which coalesces per-entity websocket updates. That addressed the *frontend*
> cost of ~5,100 entities. The two fixes are complementary — this app stops Windows and
> Chrome from starving the dashboard; `ws_state_batch` stops Home Assistant from
> flooding it. See `~/.copilot/reference/home-assistant-access.md`.

## Features

| | |
|---|---|
| **Isolated profile** | Own WebView2 user-data folder; shares nothing with Chrome or Edge |
| **No throttling** | Background timer and renderer backgrounding disabled |
| **Lives in the tray** | Close and minimise park it in the notification area instead of exiting |
| **Survives Explorer restarts** | The tray icon re-registers on `TaskbarCreated` |
| **Single instance** | Launching again focuses the running window instead of starting a second browser |
| **Auto-recovery** | Navigation failures retry with backoff (2s → 60s); a dead browser process is rebuilt |
| **Remembers its window** | Position, size and maximised state, saved on a debounce and on exit |
| **Start with Windows** | Optional, launches straight to the tray |
| **Always on top** | Toggle from the title bar or the tray menu |
| **Full screen** | `F11`, and automatically when a camera card goes full screen |
| **Stays on the dashboard** | Links that want a new window open in your real browser |

## Requirements

- Windows 10 1903 / Windows 11
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)
- .NET 10 SDK — to build only; the published app is self-contained

## Install

```powershell
pwsh -File tools\Install.ps1 -StartWithWindows
```

This publishes a self-contained build to
`%LOCALAPPDATA%\Programs\HomeAssistantDesktop` and creates a Start Menu shortcut. Both
.NET and the Windows App SDK are bundled, so the installed copy does not depend on any
machine-wide runtime.

## Build and run from source

```powershell
cd src\HomeAssistant.Desktop
dotnet build
dotnet run
```

`nuget.config` pins the repo to the Microsoft package proxy. `api.nuget.org` is blocked
by Defender network protection on these machines, and the user-level `NuGet.Config` also
lists an authenticated ADO feed that fails a non-interactive restore with 401.

No Visual Studio or Windows SDK install is needed — the XAML compiler and SDK build tools
both come from NuGet.

## Settings

Gear icon in the title bar, or **Settings…** in the tray menu. Stored in
`%LOCALAPPDATA%\HomeAssistantDesktop\settings.json`, alongside the browser profile, so
deleting that one folder resets the app completely.

### Dashboard address

Defaults to `http://192.168.1.188:8123`.

**Use the LAN address, not the public tunnel.** Home Assistant is configured with
`use_x_forwarded_for`, so a LAN client that goes out through Cloudflare and back in is
attributed to the WAN address. Ten failed logins from there and Home Assistant bans that
address — permanently, for every client on the tunnel, including your browser. A ban
answers every HTTP request with `403`, which reads exactly like a revoked token. The
dialog carries a short version of this warning.

## Keyboard

| Key | |
|---|---|
| `F5` / `Ctrl+R` | Reload |
| `F11` | Toggle full screen |
| `Esc` | Leave full screen |
| `Ctrl` + scroll | Zoom (persisted per origin by WebView2) |
| `Alt+Left` | Back |

## Layout

```
src/HomeAssistant.Desktop/
  Program.cs              Custom Main; single-instance activation redirect
  App.xaml[.cs]           Application lifetime, settings load, crash log
  MainWindow.xaml[.cs]    Title bar, WebView2 host, status overlay, settings dialog
  Services/
    AppSettings.cs        JSON settings and the well-known paths
    TrayIcon.cs           Shell_NotifyIcon on a message-only window
    WindowPlacement.cs    Save/restore of the window rectangle
    StartupManager.cs     Per-user Run key registration
    NativeMethods.cs      Win32 interop
tools/
  Install.ps1             Publish + install + Start Menu shortcut
  New-AppIcon.ps1         Regenerates Assets/app.ico
```

### Two things worth knowing before editing

**`AppWindow.Presenter.Kind` is `Overlapped`, never `Default`,** for a normal window.
`Default` is a value you pass to `SetPresenter`, not one you read back. An early version
guarded `SavePlacement` with `Kind != Default` and silently never saved anything.

**`GetWindowPlacement`/`SetWindowPlacement` are not in physical pixels.** On a 125%
display a window physically at `300,180 1500x980` reads back as `375,225 1875x1225`. The
two agree with each other, so capture/restore is exact — but their values must never be
compared against `GetWindowRect` or `GetMonitorInfo`, which *are* physical. The off-screen
sanity check therefore runs *after* `SetWindowPlacement`, against `GetWindowRect`, and the
first-run default goes through `DisplayArea` + `AppWindow.MoveAndResize` instead.

## Troubleshooting

**"Could not start the browser engine"** — the WebView2 Runtime is missing, or another
instance has the profile folder locked. The app is single-instance, so the usual cause is
a stale `msedgewebview2.exe`.

**The tray icon is missing.** Windows 11 hides new tray icons in the overflow flyout by
default. Taskbar settings → Other system tray icons → turn on *Home Assistant*.

**It will not come back from the tray.** Launch it again; the second launch redirects to
the running instance and restores the window.

**Crashes** are appended to `%LOCALAPPDATA%\HomeAssistantDesktop\crash.log`.
