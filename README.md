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
| **Always on top** | Toggle from the tray menu, or the settings dialog |
| **Full screen** | `F11`, and automatically when a camera card goes full screen |
| **Stays on the dashboard** | Links that want a new window open in your real browser |
| **Native notifications** | Home Assistant pushes over a websocket channel; action buttons report back as `mobile_app_notification_action` |
| **Unread count** | On the taskbar button and the tray icon; clears when the dashboard is back on screen |
| **Idles when it cannot be seen** | Stops rendering when fully covered, locked, asleep, or the display is off |

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

### Locked, asleep, or display off

`bench/Run-LockSleep.ps1`. The occlusion watcher cannot see any of these: a locked
workstation runs on a separate desktop, so our window is neither covered nor hidden by
anything it can enumerate, and it would keep compositing at the display refresh rate
to an audience of nobody.

`SystemStateWatcher` takes `WM_WTSSESSION_CHANGE` and `WM_POWERBROADCAST` on a
message-only window, covering session lock/unlock, monitor power, and sleep/resume.

| state | CPU | frames | timers |
|---|---:|---:|---:|
| unlocked | 42.64% | 144 fps | 4.03/s |
| **session locked** | **4.17%** | 0 fps | **4.00/s** |
| unlocked again | 37.17% | 144 fps | 4.00/s |

Sleep is a correctness problem rather than a cost one, so resume is handled separately:
the network went away with the machine, and the page's websocket is stale however
healthy it looks, so `PBT_APMRESUME*` forces a reload instead of trusting it.

The test does not lock the workstation. It posts those messages to the watcher's window
directly, which is the same code path a real lock takes without locking anyone out.

## Native notifications

Home Assistant pushes to this machine the same way it pushes to the family's phones, so
the dashboard does not have to be on screen — or even running in the foreground — for a
notification to arrive.

The app registers itself with Home Assistant as a `mobile_app` device and opens a
websocket push channel. That choice matters: the alternative, HTTP push, needs a URL
Home Assistant can reach, which means a listening port on this machine. A websocket
channel is outbound-only, so there is nothing to expose and nothing to firewall.

```powershell
pwsh -File tools\Register-TickerTarget.ps1
```

That registers the device, creates `device_tracker.<machine>` and
`notify.mobile_app_<machine>`, attaches the tracker to a person so notifications
addressed to that person reach this desktop, and stores the webhook id in the app's
settings. The app borrows the dashboard's own access token out of the page, so it needs
no credential of its own.

Toasts carry the message, title, an optional inline image, and action buttons. Pressing
a button raises `mobile_app_notification_action` back in Home Assistant, which is the
same event the phones raise, so existing automations work unchanged. Clicking the body
navigates the dashboard to `navigate_to`. A message of `clear_notification` dismisses by
tag.

Test delivery against this machine only:

```powershell
# Never use ticker.notify for a test - it fans out to everyone's phones.
$body = @{ message = 'Test'; title = 'Home Assistant' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$ha/api/services/notify/mobile_app_dswett_home" `
    -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body
```

To check the action buttons specifically:

```powershell
pwsh -File tools\Test-ToastAction.ps1
```

That sends a toast and waits for the press to come back as
`mobile_app_notification_action`. The press has to be a real one — Windows renders
toasts in an isolated accessibility tree that an ordinary process cannot traverse or
click, so this is the one step that cannot be automated.

`%LOCALAPPDATA%\HomeAssistantDesktop\app.log` records registration, channel state, and
every toast. None of this has a UI, so the log is the only way to tell working from
quietly broken.

### Unread count

Notifications that arrive while the dashboard is not on screen raise a count, shown in
two places because neither covers the app's whole life:

- the **taskbar button**, via the shell's overlay icon
- the **tray icon**, redrawn with the count in its corner

The tray is not a nicety. Parking the app hides the window, which takes its taskbar
button with it, and that is how the app spends most of its time — so a taskbar badge
alone would be invisible exactly when it mattered. Windows App SDK's
`BadgeNotificationManager` is not used at all: it expects package identity this app
deliberately does not have.

Tagged notifications are tracked by tag rather than counted, so a sensor re-sending
under the same tag replaces its predecessor instead of inflating the badge — one
doorbell is one badge however many times it fires. The count clears when the dashboard
comes back on screen.

Whether the dashboard is "on screen" is asked live rather than tracked from WinUI's
`Activated` event. Parking the window calls `AppWindow.Hide()`, for which WinUI raises
no deactivation, so a cached flag stays stuck on "active" and nothing is ever counted.
The same predicate decides both counting and clearing, so the two cannot disagree.

### What Windows calls the app

An unpackaged app has no manifest to read a name out of, so `AppNotificationManager`
invents one: it takes the executable's file name and truncates it at the first dot.
`HomeAssistant.Desktop.exe` therefore labelled every toast "HomeAssistant", with no
space. The `Register(displayName, iconUri)` overload sets both explicitly, and the name
and icon have to be supplied together — there is no overload for one without the other,
and the platform rejects an icon path that does not exist. Registration falls back to
the parameterless form if `Assets\app.ico` is missing, since a badly labelled toast
still beats no toast at all.

### Three ways this fails silently

Each of these was hit, and none of them reports anything on its own.

**The missing DLL.** Windows App SDK 2.5.1's self-contained deployment omits
`Microsoft.WindowsAppRuntime.Insights.Resource.dll`, and no package in the dependency
graph carries it, so it cannot simply be referenced. Nothing notices until
`AppNotificationManager.Register()` throws `0x8007007E`, and the app loses toasts
entirely ([WindowsAppSDK#6774](https://github.com/microsoft/WindowsAppSDK/issues/6774)).
`tools/Copy-InsightsResource.ps1` lifts the file out of the installed runtime package at
build time. It must match the build architecture: all architectures share a version
number, so an unfiltered "newest" pick cheerfully drops the x86 copy beside an x64 app,
where it will not load.

**The reused message id.** Home Assistant requires every websocket message id on a
connection to be strictly greater than the last, and silently drops any that is not.
Sending each delivery confirmation with a hardcoded id meant only the first was ever
processed. The second notification then went unconfirmed, and `PUSH_CONFIRM_TIMEOUT`
(10s) made Home Assistant conclude the device was unreachable, tear the channel down,
and fail every later send with a 500. The first notification always worked, which is
what made it confusing.

**Notifications switched off.** `AppNotificationManager.Show()` reports nothing when the
platform discards a toast, and `AppNotification.Id` stays `0`. But when notifications are
disabled for the *account*, the toast is accepted — a real id comes back — and still
never appears. The one reliable signal is `ToastNotifier.Setting`, which reads
`DisabledForUser`. The switch is Settings → System → Notifications, and it is global
rather than anything to do with this app:

```powershell
(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications').ToastEnabled
```

`0` means no app on the machine can raise a toast.

## The companion app bridge

Signed in, the app attaches Home Assistant's external app bridge — the same one the
Android and iOS companion apps speak. Two things follow from that.

**The app owns authentication.** The frontend decides it is running inside a native
app purely by finding `window.externalApp` on the page (`src/data/external.ts`), and
from then on it stops loading tokens from its own storage and asks the host for one
instead (`src/entrypoints/core.ts` chooses `createExternalAuth` over `getAuth`). That
is what lets a laptop move between the internal and external addresses without being
asked to log in again: those are different web origins, and the page's own session
does not cross them. The app's token does.

**Home Assistant shows a "Companion App" row** in Settings and in the sidebar. The
frontend asks what the app can do, over the bridge, and renders accordingly:

| Message | Direction | |
|---|---|---|
| `config/get` | frontend → app | answered with `hasSettingsScreen: true` |
| `config_screen/show` | frontend → app | opens this app's settings dialog |
| `getExternalAuth` | frontend → app | answered with an access token and its lifetime |
| `revokeExternalAuth` | frontend → app | signs the app out |

Two details are load-bearing:

- `config/get` **must** be answered. The frontend awaits it inside
  `createExternalAuth`, so a missing reply leaves the dashboard permanently blank
  rather than merely missing a feature.
- The bridge is only installed when the app actually holds credentials. Once
  `window.externalApp` exists the frontend will not fall back to its own session, so
  installing it without a sign-in behind it would turn a dashboard that logs itself
  in into one that cannot log in at all. Signing in or out rebuilds the WebView so
  the two stay in step.

Only `hasSettingsScreen` is advertised. The frontend asks follow-up questions only
about capabilities the app claims, so the list is kept honest rather than aspirational.

### Which pages count as Home Assistant

The bridge hands out real Home Assistant access tokens, so the question of who is
asking has to be answered rather than assumed. It had been assumed, and that was wrong
in two ways that a notification could reach.

The injected script runs in **every** top-level document the browser loads, not only
Home Assistant's — that is simply what `AddScriptToExecuteOnDocumentCreatedAsync` does.
Nothing checked where a request came from before answering it with a token, so any page
the window could be made to visit could ask for one and get it. `NavigateToPath` was how
you made it visit one: it resolved with `Uri.TryCreate(base, target)`, which returns the
target whenever the target is absolute, so a notification carrying
`navigate_to: "https://example.test/"` moved the window there on a click.

Worse, and needing no click at all, a notification also names the address its picture
comes from. That address was used exactly as given and the bearer token was attached to
the request, so a notification could name any host and be sent a live token the moment
it arrived — before the toast was even on screen, and whether or not the app had signed
in, since the token provider falls back to borrowing the dashboard's.

`TrustedOrigins` now answers the question in one place, and origin means what the web
means by it: scheme, host and port, all three. Both configured addresses count, because
a laptop that left the house is on the external one and is no less itself for it.

- A notification's picture is still fetched if it lives elsewhere, as it is on the
  phones — but off Home Assistant it is fetched without credentials.
- A link that resolves off Home Assistant is opened in the browser rather than in this
  window, which is what already happened to links asking for a window of their own.
- Top-level navigation away from Home Assistant is refused and handed to the browser.
- The bridge checks the sending document before answering anything, so even a page that
  arrives some other way is refused.
- The reply callback must be one of the two names the frontend actually uses. It is
  interpolated into a statement that is then run in the page, and the reply is not
  delivered to the document that asked — a token round trip can outlast a navigation —
  so an unexpected name is refused rather than echoed.

That last point is the reason the origin check lives in the bridge rather than only in
the navigation guard: the guard is about where the window goes, and the bridge is about
who it talks to. They are not the same question, and only the second one protects a
token.

### Taskbar jump list

Right-clicking the app on the taskbar can carry up to ten entries of your own, each
pointing at a dashboard, a view, or a single entity. Configured under **Taskbar** in
Settings, which offers your real dashboards and entities to pick from — read over the
dashboard's own websocket, so it needs no second connection and can only offer what
the signed-in account can reach.

An entity entry does one of two things, chosen per entry:

| | |
|---|---|
| **Open it** | Brings the window up with that entity's dialog showing |
| **Toggle it** | Calls `homeassistant.toggle` and leaves the window alone |

Two details are less obvious than they look.

There is no URL that opens an entity. `/_my_redirect/more_info` answers `200`, but so
does every other frontend path, and `more_info` is not in the redirect table at all.
The frontend's own route in is a `hass-more-info` event — and that event is one-shot,
silently dropped if it arrives before the shell has attached its listener, which is
exactly the race a cold launch creates. Home Assistant's own end-to-end tests solve
this by dispatching repeatedly until the dialog appears, and so does this.

Toggling does not involve the page at all. It is a REST call with the app's own
credentials, so it still works when the app was launched purely to do that one thing
and the dashboard has not loaded.

The list is published through the Win32 `ICustomDestinationList`. The modern
`Windows.UI.StartScreen.JumpList` needs package identity, which an unpackaged app does
not have.

### Thumbnail toolbar buttons

Hovering the taskbar button shows a preview of the window with up to seven buttons
beneath it, configured the same way as the jump list and pointing at the same three
kinds of thing. Each one also carries an icon, picked from a short palette of Segoe
Fluent glyphs, because the shell shows no text — the title becomes the tooltip.

The shell will accept buttons for a window exactly once, and only after it has sent
`TaskbarButtonCreated`, which it re-sends if Explorer restarts. So all seven are
registered the moment that message arrives, and the ones you have not configured are
registered hidden; changing the set later updates those same seven in place. Clicks
come back as `WM_COMMAND` on the main window, which means subclassing it.

The glyph palette was checked rather than assumed. Ink coverage cannot tell a real
glyph from tofu, since a missing codepoint still draws something; `GetGlyphIndicesW`
with `GGI_MARK_NONEXISTING_GLYPHS` can, and it reported one of the candidates absent
from the shipped font.

### Global hotkey

A combination of your choosing brings the window up from anywhere, and puts it away
again when it is already in front — not when it is merely open, since a summon key
that hides the window you were looking for is a trap. Nothing is bound by default:
taking a combination the user has not asked for takes it away from whatever they
already use it for.

The picker binds as you press, because Windows is the only authority on whether a
combination is free — it hands each one to a single window, first come first served.
Registering is the only way to find out, so a combination another program already
holds is reported there and then rather than saved and silently ignored. Cancelling
the dialog puts the previous binding back.

### `homeassistant://` links

A link opens the app and takes it somewhere, using the spellings Home Assistant's own
companion apps use, so a link written for a phone works here unchanged:

| | |
|---|---|
| `homeassistant://navigate/energy` | Opens a page. No leading slash after the host |
| `homeassistant://navigate/?more-info-entity-id=light.kitchen` | Opens an entity's dialog |
| `homeassistant://call_service/light.turn_on?entity_id=light.kitchen&brightness=200` | Calls a service |

Two of those are easy to get wrong from memory. There is no `more-info` host — an
entity is a query parameter on a *root* navigate, spelled with hyphens, and the
companion apps honour it only when no path is given. And `call_service` takes the
domain and service as one dot-joined segment, not two path segments.

A service call takes its data from the query string and goes straight to the REST API
rather than through the page, so it works whether or not the dashboard has loaded —
and it deliberately does not bring the window up, since changing something is no
reason to interrupt what is on screen. Values that read as numbers or booleans are
sent as such; Home Assistant rejects `"50"` where a service wants a number.

Anything else is ignored rather than guessed at. That matters most for
`homeassistant://auth-callback`, which Home Assistant's server reserves as the
companion apps' OAuth redirect: a link arriving there would be carrying someone
else's authorisation code. This app's own sign-in uses a different redirect entirely.

The handler is registered per-user by the installer rather than by the app, so that
uninstalling takes it away again and nothing needs elevation.

## Updates

The app checks GitHub for new releases and offers them **inside Home Assistant**, in
Settings → Updates, alongside Home Assistant's own. Pressing Install downloads the
installer, applies it and restarts the app.

It does that by publishing an MQTT `update` entity describing itself. That is not a
stylistic choice — it is the only way an outside program holding nothing but an access
token can create a *working* update entity:

| | |
|---|---|
| `mobile_app` | Registers sensors and binary sensors only. The webhook rejects any other type |
| `POST /api/states/update.x` | Creates a state with no entity behind it, so the Install button calls a service that matches nothing and silently does nothing |
| MQTT discovery | A real entity, owned by the MQTT integration, with a working `async_install` |

Three details are easy to get wrong.

`command_topic` is what turns the Install button on — without it the entity is a
read-only notice, which is why an update entity can appear to work and then do nothing.
But `payload_install` has **no default**: set the topic without it and Home Assistant
shows the button and raises inside the integration when it is pressed. Set both or
neither.

Pressing Install publishes to that topic, so the app has to be listening. It subscribes
over the Home Assistant websocket it already holds, using `mqtt/subscribe`, which needs
no broker credentials — only the token the app already has. That subscription is
**administrator-only**; publishing is not. A non-admin account still gets the entity and
still sees new versions, and the app says so in its log rather than leaving a button
that quietly does nothing.

Installing needs one more trick. An MSI upgrade replaces the running executable, so the
work is handed to a helper that outlives the app: it waits for the app to exit, runs the
installer, and starts the new build. The app will not update a copy running from a build
folder, since the installer would replace the *installed* app and the helper would then
start that one instead.

### The close that was not a close

The installer asks the app to shut down with the end-session message rather than a plain
close. A plain close is what the window's own close button sends, and this app answers
that by hiding to the notification area — so the request was honoured, the process
stayed, and every file it held stayed locked. Measured before the fix: the app survives
`WM_CLOSE` with its process intact.

This had never shown up because every install so far stopped the app by hand first.

## Requirements

- Windows 10 1903 / Windows 11
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11)
- .NET 10 SDK — to build only; the published app is self-contained

## Install

On the machine you build from:

```powershell
pwsh -File tools\Install.ps1 -StartWithWindows
```

This publishes a self-contained build to
`%LOCALAPPDATA%\Programs\HomeAssistantDesktop` and creates a Start Menu shortcut. Both
.NET and the Windows App SDK are bundled, so the installed copy does not depend on any
machine-wide runtime.

### Installing on other machines

Build an MSI and carry that instead:

```powershell
dotnet tool install --global wix --version 5.0.2 --configfile nuget.config
pwsh -File tools\Build-Installer.ps1
```

That produces `dist\HomeAssistantDesktop-<version>-x64.msi`, about 75 MB — the
self-contained app compresses well. Then on each machine:

```powershell
msiexec /i HomeAssistantDesktop-1.0.0-x64.msi /qb STARTWITHWINDOWS=1
pwsh -File Register-TickerTarget.ps1 -HaUrl http://<ha-address>:8123 -Token <token>
```

The install is **per user**: it lands in `%LOCALAPPDATA%\Programs`, starts from HKCU, and
needs no administrator. There is nothing about the app that belongs to the machine, and
not needing elevation is the point when putting it on several of them. It appears in
Add/Remove Programs, upgrades in place, and closes a running copy first so an upgrade
never asks for a reboot. Uninstalling removes the app but leaves your settings.

`STARTWITHWINDOWS=1` is optional and opt-in; you can also tick the box in the app's own
settings later.

### SmartScreen, and why the installer is not signed

A browser-downloaded MSI arrives with a `Zone.Identifier` alternate data stream — the
Mark of the Web — and that is what makes SmartScreen look the file up. Nothing is
signed here, so the lookup finds no publisher and no reputation, and Windows offers
only **More info → Run anyway**.

Signing would not fix it. Microsoft removed EV certificates' instant SmartScreen bypass
in 2024, so Azure Artifact Signing, OV and EV certificates now behave identically:
warnings are expected on new files, and trust accrues only as real people download
them. An app that exists on a handful of machines never accumulates that, so a
certificate would buy a publisher name rather than a clean install.

Two things make it a non-issue in practice.

**Updates never see it.** `UpdateInstaller` fetches the MSI with `HttpClient`, and only
browsers and the attachment manager attach the Mark of the Web — so the downloaded file
carries none, and `msiexec` runs without a prompt. Updating through the Install button
on the update entity is the normal path, and it is already clear.

**A first install can drop the mark deliberately**, which is a statement that you know
where the file came from:

```powershell
Unblock-File .\HomeAssistantDesktop-1.3.2-x64.msi
msiexec /i HomeAssistantDesktop-1.3.2-x64.msi /qb
```

Downloading with `gh release download` or `Invoke-WebRequest` instead of a browser has
the same effect, for the same reason.

**Each machine registers as its own device.** The registration script names the device
after `%COMPUTERNAME%`, so every machine gets its own `device_tracker.<machine>`,
`notify.mobile_app_<machine>` and push channel, and Home Assistant can address them
separately or together through a person. Attach each one to the same person and a
notification sent to that person reaches all of them.

Pin WiX to 5. Versions 6 and later require accepting the Open Source Maintenance Fee
EULA; 5 is the last release under the plain MS-RL.

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

## Continuous integration

Two workflows, both on `windows-latest`.

**CI** (`.github/workflows/ci.yml`) runs on every push to `main` and every pull request:

- `tools/Test-Scripts.ps1` — parses every `.ps1`, checks every XML file is well formed,
  and runs PSScriptAnalyzer
- builds and publishes the app, then asserts the publish output contains the three files
  whose absence breaks it silently: the exe, the project `.pri`, and the Insights
  resource DLL

**Release** (`.github/workflows/release.yml`) runs on a `v*` tag, builds the MSI, checks
the tag matches the project version, and attaches the MSI to a GitHub release.

Two things make a hosted runner different from a development machine, and both are
handled explicitly rather than discovered later:

- **The package feed.** `nuget.config` points at the Microsoft package proxy, which a
  hosted runner cannot reach, while `api.nuget.org` — which it can — is blocked by
  Defender locally. CI passes `build/nuget.ci.config` rather than weakening the local
  one.
- **The Windows App Runtime.** The app is self-contained and needs no runtime to *run*,
  but it needs one to *build*, for the single file the SDK omits. A runner has none, so
  `build/Install-WindowsAppRuntime.ps1` installs it first. `Build-Installer.ps1` refuses
  to build without that file, so a release fails rather than shipping an MSI whose
  toasts are silently dead.

`tools/Test-Scripts.ps1` holds `bench/` to a lower bar than the rest — it must parse,
but its empty catch blocks are deliberate: best-effort cleanup of test windows should
never derail a measurement run.

What CI does **not** cover is most of what matters here. Occlusion, lock and sleep
behaviour, toast delivery, action buttons and the badges all need a real desktop, a real
Home Assistant and the Windows shell. Those are verified by hand, by the methods in the
sections above.

## Releasing

```powershell
# 1. Bump <Version> in src\HomeAssistant.Desktop\HomeAssistant.Desktop.csproj
# 2. Land that through a PR
# 3. Tag it
git tag v1.0.1
git push origin v1.0.1
```

The tag drives everything else. The workflow fails if the tag and the project version
disagree, so the two cannot drift.

## Settings

**Settings…** in the tray menu, or **Companion App** in Home Assistant's own Settings
once the app is signed in. A gear appears in the title bar only when it is not, and
the status overlay carries its own Settings button whenever the dashboard cannot be
reached. Stored in
`%LOCALAPPDATA%\HomeAssistantDesktop\settings.json`, alongside the browser profile, so
deleting that one folder resets the app completely.

### Dashboard address

Two addresses: the one on your own network, and an optional one that works from
anywhere. The local address is used whenever it answers, and the external one only
when it does not, so a laptop keeps working when it leaves the house. **Fill in from
Home Assistant** reads both out of Home Assistant's own configuration.

There is no built-in default. A machine that has not been told where Home Assistant
lives says so, rather than quietly failing to reach somebody else's.

**Prefer the LAN address.** Home Assistant is typically configured with
`use_x_forwarded_for`, so a LAN client that goes out through a tunnel and back in is
attributed to the WAN address. Ten failed logins from there and Home Assistant bans that
address — permanently, for every client on the tunnel, including your browser. A ban
answers every HTTP request with `403`, which reads exactly like a revoked token. The
dialog carries a short version of this warning.

### Require Windows Hello

Off by default. When on, the app shows a lock screen at launch and asks for Windows
Hello before revealing the dashboard. The browser still starts underneath, so
notifications and the unread count keep working while locked — holding those back
would make a locked app useless rather than private.

This is a screen lock, not a vault. The refresh token is protected with DPAPI, which
is bound to your Windows account, so anything already running as you can read it with
or without this. What it covers is the case it describes: an unattended, unlocked
machine with your house on screen.

The setting can only be switched on after a successful verification, so turning on a
lock cannot be the thing that locks you out. If Hello is later removed from the
machine, the app opens unlocked and says so in the log rather than stranding you.

## The title bar

The title bar takes its colour from the dashboard, so the window reads as one surface
rather than a strip of Windows sitting above a strip of Home Assistant.

The colour is the page's, not a guess at one. Home Assistant resolves its header
colour into `<meta name="theme-color">` every time a theme is applied, and paints its
own header text with `--app-header-text-color`; the app follows both. Following the
text colour matters as much as the background: Home Assistant puts white on its light
blue default header, where a contrast calculation would pick black and immediately
look like a different application.

A small injected script watches that meta tag and reports changes, rather than the
external bus's `theme-update` message, for two reasons — the bus message carries no
payload, so the colour has to be read from the page anyway, and the bus only exists
when the app is signed in. Watching the tag covers the sign-in page too, and catches
the automatic switch between a light and a dark theme.

Whatever the theme holds is resolved through a throwaway element first, so the host
only ever parses the `rgb()` triple the browser normalises it to rather than trying to
understand every CSS colour syntax. Until the page reports anything — a cold start, an
address that has not loaded — the title bar stays on the system theme, which is the
right look for a window with nothing in it yet.

Windows draws the minimise, maximise and close buttons itself, so those are coloured
separately; their hover and pressed tints are mixed from the header colour towards the
header's text colour, which is what keeps them visible on a light theme as well as a
dark one.

### High contrast

None of this applies in a high contrast theme. High contrast exists so that a chosen
pairing of colours is honoured everywhere, and an app that overrides it with a colour
of its own — however carefully matched to the page behind it — takes that away. So the
tint is dropped, the caption button colours are set back to `null`, which is what asks
Windows for its defaults, and the title bar renders in the system scheme.

It is read through `SystemParametersInfo(SPI_GETHIGHCONTRAST)` rather than
`AccessibilitySettings`, which wants a `CoreWindow` that an unpackaged desktop app does
not have, and re-read on `WM_SETTINGCHANGE` so turning high contrast on repaints the
title bar immediately instead of at the next restart.

Verified by measuring the title bar's dominant pixel: `rgb(28,28,28)` following the
dashboard normally, `rgb(32,32,32)` under high contrast — which is exactly the system's
`COLOR_WINDOW` — and back to `rgb(28,28,28)` afterwards.

### Screen readers

The status and lock overlays replace the dashboard without moving focus, so a screen
reader would otherwise say nothing at all while the window stopped showing what it was
showing. Both announce themselves through `RaiseNotificationEvent`, raised from the
root element because a peer on a collapsed one is not reliably listened to. The lock
overlay also takes focus, which it needs anyway: leaving focus inside the hidden
dashboard made the only thing on screen unreachable by keyboard.

## Keyboard

The title bar is bare. Settings has its own entry in the tray menu, and — once the
app is signed in — a **Companion App** row in Home Assistant's own Settings and
sidebar, put there by the external app bridge. A gear appears in the title bar only
when the app is signed out, where neither of those exists.

Back, Home, Reload and Always on top each had a button once, and every one of them
was reachable without it — the keys below, Home Assistant's own sidebar, and the tray
menu — so a dashboard you keep on screen permanently is better off without a toolbar.

| Key | |
|---|---|
| `F5` / `Ctrl+R` | Reload |
| `F11` | Toggle full screen |
| `Esc` | Leave full screen |
| `Alt+Left` | Back |
| `Alt+Space` | Window menu |

Every one of those is handled by the app. They used to come free from WebView2's
browser accelerator keys, which also brought Ctrl+P, Ctrl+F, Ctrl+S and Ctrl+O with
them — a print preview and a find bar are a browser showing through. Turning that
setting off took reload and Back with it, so both are raised from the injected key
handler instead and behave exactly as before.

## What a browser does that this does not

A WebView2 arrives with a browser's habits, and most of them are wrong for an app
window. Beyond the status bar, autofill, password saving and Chromium error pages,
four were worth handling by name.

**The context menu.** Right-clicking the dashboard used to offer Back, Reload, Save
as, Translate, View source and the rest. Switching context menus off removes all of
that — and takes Cut, Copy and Paste in Home Assistant's text fields with it, which no
Windows app is allowed to do. So the browser menu stays on and is filtered instead: the
editing commands survive, everything else is dropped, and a right-click on ordinary
content shows nothing at all. Separators left stranded by the filtering are removed
too, so the menu never opens with a dividing line at the top.

Chromium's item names are matched rather than its labels, because the labels are
localised. If a text field ever comes back with no editing commands this app
recognises — which would mean those names have moved on — the whole browser menu is
shown unfiltered and a line is written to the log. A text box with a few browser
commands on it is a far better failure than a text box with no Paste.

With developer tools enabled in Settings, the full menu is left alone; Inspect is the
point of that switch.

**Zoom.** `Ctrl` with the wheel or the plus key, and a pinch on a touchpad, all scaled
the page. On a dashboard that is a way to knock the layout askew by accident with no
obvious way back — and it was never persisted, so it lasted until the next restart and
no longer. Home Assistant does its own scaling, so nothing is lost.

**A dropped file.** WebView2 accepts an external drop wherever the page does not claim
it, and navigates to whatever was dropped: a file landing anywhere on the window took
Home Assistant off screen until it was reloaded. Refusing the navigation is narrower
than refusing the drop, so Home Assistant's own upload targets — a backup, a media
file — still work.

**Downloads.** Home Assistant serves real ones, and each raised Edge's download
flyout. The file still lands in the Downloads folder; it is now shown in Explorer when
it arrives, the way a native app would.

## The window menu

Right-clicking the title bar and pressing `Alt+Space` both raise the window menu —
Restore, Move, Size, Minimize, Maximize, Close — on any ordinary window. Extending
content into the title bar replaces the non-client area with plain XAML, and WinUI does
not reimplement that part, so both did nothing at all.

The menu shown is the real one. `GetSystemMenu` returns the window's own, so the items,
their order, their accelerators and their localisation stay the system's rather than an
imitation that would drift. Windows normally greys out what does not apply as it opens
the menu, which it is not doing here, so the app sets those states itself — otherwise a
maximized window offers to maximize again.

`Alt+Space` is raised from the injected key handler rather than from `WM_SYSKEYDOWN`.
Keyboard messages go to whatever has focus, and that is the browser's own child window,
not this one.

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
