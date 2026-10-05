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

## Measured against the Chrome app

Harness in `bench/`. Everything is read from outside the processes under test —
window titles and OS process counters — so both hosts are measured by the same
method with nothing instrumented or attached. Whole process trees are counted, not
just the window owner. Measured on DSWETT-HOME, 4K at 144Hz.

### Timer fidelity while hidden — the one that matters for a tray app

`bench/Run-Throttling.ps1`. The same page runs a 250ms interval (4 ticks/s) in each
host; all are hidden for 120s and sampled throughout.

| host | visible | hidden | retained | ticks lost |
|---|---:|---:|---:|---:|
| Chrome app window, your Chrome | 4.02/s | 0.80/s | 20% | 384 |
| Chrome app window, isolated Chrome | 4.00/s | 0.80/s | 20% | 384 |
| **this app** | 4.05/s | **4.00/s** | **100%** | **0** |

Chrome drops to exactly 1.00/s on hiding, then to 0.73/s as intensive throttling
engages. Both Chrome variants behave identically, so this is Chromium policy, not
contention. The app loses nothing, which is the whole reason for the three
`--disable-*-throttling`/`backgrounding` flags.

### Cost of the same work

`bench/Run-EngineOverhead.ps1`. Identical local page, identical window size, warm
profiles, each host measured alone with its window in the foreground. Both rendered
at the same rate, so this compares like with like.

| host | CPU | memory | processes | fps |
|---|---:|---:|---:|---:|
| **this app** | **39.6%** | **532 MB** | 7 | 143.7 |
| Chrome, isolated | 56.1% | 769 MB | 16 | 143.7 |

About 30% less CPU and 30% less memory for the same frames.

### Idle in the tray

`bench/Run-CpuBreakdown.ps1`. This is where an always-on app actually lives.

| state | CPU | note |
|---|---:|---|
| visible, dashboard | 213% | renderer 113% + GPU 98%, rendering at 144Hz |
| **parked in the tray** | **3.95%** | 98% drop; renderer working set 757 → 300 MB |

Worth being precise about why this is not a contradiction with the section above:
disabling background throttling keeps **timers** at full rate, but frame callbacks
and compositing still stop when the window is genuinely not on screen. The app stays
current without repainting something nobody is looking at.

### Measurements that did not show a difference

Stated because they were run, not because they help. `bench/Run-Contention.ps1`
loaded three GPU-heavy windows inside Chrome and measured frame health in all three
hosts: nothing degraded, in any host. On this hardware browser load was not enough
to starve the compositor, so the shared-GPU-process argument below is structural,
not something this benchmark demonstrated.

### Measurement traps hit along the way

Each of these produced a confident, wrong number first:

- **The two hosts restore different dashboards.** Each profile reopens whatever view
  it last had, so one was rendering a live-updating list and the other a static view.
  A first pass read 210% vs 35% and was pure artefact. Pin an explicit URL and assert
  the two window titles match before believing anything.
- **Chrome reuses an existing renderer** for a site it already has open, so
  identifying "the dashboard's renderer" by diffing renderer PIDs finds a spare
  process at 0.07% CPU instead of the real one.
- **A covered window stops working in Chrome but not in this app**, by design. Any
  visible-state comparison has to have each window genuinely in the foreground, which
  in practice means measuring the hosts sequentially rather than side by side.
- **A cold Chrome profile renders well below the display rate** while it builds its
  shader cache — 51.8 fps against 143.7 — which makes its CPU look far better than it
  is. Warm the profile first.
- **`Win32_Process` reports PIDs as `UInt32`.** A `UInt32` key does not match an
  `Int32` lookup in a .NET hashtable, so a process-tree walk silently returns only the
  root and every "tree" total is really just one process.

### What this does not claim

The structural isolation is real and verifiable — separate browser, GPU, network and
renderer processes, separate profile, confirmed by process tree. Your Chrome serves
every window it owns from **one** GPU process; this app has its own. That makes the
dashboard independent of Chrome's fate.

But the historical stutter was largely a *server-side* problem, already fixed by the
`ws_state_batch` component described above. This app does not take credit for that.

### Not painting what you cannot see

`bench/Run-Occlusion.ps1`. The largest remaining waste, and the one thing here that
is not just "Chromium in a different box".

Chromium has occlusion detection and a switch to turn it off, but **neither applies
in this app**: WinUI hosts WebView2 in composition mode, so the browser has no
top-level window of its own to test. Measured on a fully covered window, the page
kept rendering at 143.6 fps and 43.8% CPU with `--disable-backgrounding-occluded-windows`
on *and* off. The flag is a no-op here in both directions.

So `WindowVisibilityWatcher` makes the call host-side — minimised, cloaked to another
virtual desktop, or entirely covered by a window above it — and collapses the WebView2,
which suspends rendering while leaving the page running.

| state | CPU | frames | timers |
|---|---:|---:|---:|
| uncovered | 38.70% | 144.0 fps | 4.00/s |
| **covered, watcher on** | **3.75%** | 0 fps | **4.00/s** |
| covered, watcher off | 35.89% | 143.6 fps | 4.01/s |

On the real dashboard the same change is **215% → 3.44% CPU**, a saving of roughly two
cores whenever the window sits behind something. Timer fidelity is untouched, so the
dashboard is current the instant it is uncovered.

Set `RenderWhenCovered: true` in settings.json to disable this.

There is a deliberate safety net: if the app is the foreground window it is never
judged covered, whatever the geometry says. A translucent or oddly shaped window that
happened to enclose our rectangle would otherwise freeze the dashboard while it was
plainly on screen, and a stale dashboard is a much worse failure than a wasted frame.

### Things that turned out not to be worth doing

- **Reduced motion.** `--force-prefers-reduced-motion` on the real dashboard: 229.7% →
  232.3%, i.e. nothing. Home Assistant does not drive its expensive repaints from
  anything that honours the media query. Available as `ReduceAnimations` but off.
- **Chasing the GPU.** `edge://gpu` reports Canvas, Compositing, Rasterization, Video
  Decode and WebGL all hardware accelerated on the RTX 5090, with no software fallback.
  There was nothing misconfigured to fix.
- **The dashboard's own cost.** ~230% CPU visible, against ~45% for a trivial page in
  the same window at the same frame rate. That gap is the content, not the host, and
  Chrome pays it too. It is not something a wrapper can optimise away.

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
