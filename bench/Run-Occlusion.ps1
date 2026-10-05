# What occlusion costs, and whether data freshness survives turning it off.
#
# The app disables Chromium's occluded-window backgrounding so a dashboard behind
# other windows stays current. The risk is that it also keeps rasterising and
# compositing a 4K surface at 144Hz that nobody can see.
#
# This measures both halves of that trade in each configuration: CPU of the whole
# process tree, and timer fidelity from the bench page's own counters. The window is
# covered by a maximised, static Chrome window - genuinely occluded, not minimised,
# which is the case the flag actually governs.
#
# Run once with -RenderWhenCovered and once without.

param(
    [int]$Seconds = 40,
    [switch]$RenderWhenCovered,
    [switch]$ReduceAnimations,
    # Defaults to the bench page, which reports its own frame and timer counters.
    # Point at the real dashboard to measure the cost that actually matters; the
    # fps and ticks columns read zero there, since only the bench page publishes them.
    [string]$Url
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$benchUrl = 'file:///' + ((Join-Path $here 'bench.html') -replace '\\', '/')
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

$SW_MAXIMIZE = 3

function Measure-State {
    param([int]$RootPid, [string]$BenchId, [int]$Seconds)

    $pids = Get-ProcessTreePids -RootPid $RootPid
    $before = @{}
    foreach ($id in $pids) { try { $before[$id] = (Get-Process -Id $id -EA Stop).TotalProcessorTime } catch { } }
    $r0 = Get-BenchReading -Id $BenchId
    $t0 = [DateTime]::UtcNow

    Start-Sleep -Seconds $Seconds

    $wall = ([DateTime]::UtcNow - $t0).TotalSeconds
    $r1 = Get-BenchReading -Id $BenchId
    $cpuSeconds = 0.0
    foreach ($id in $pids) {
        try {
            $p = Get-Process -Id $id -EA Stop
            if ($before.ContainsKey($id)) { $cpuSeconds += ($p.TotalProcessorTime - $before[$id]).TotalSeconds }
        }
        catch { }
    }

    $pageSecs = $r1.Elapsed - $r0.Elapsed
    [pscustomobject]@{
        Cpu    = [math]::Round(($cpuSeconds / $wall) * 100, 2)
        Fps    = if ($pageSecs -gt 0) { [math]::Round(($r1.Frames - $r0.Frames) / $pageSecs, 1) } else { 0 }
        Ticks  = if ($pageSecs -gt 0) { [math]::Round(($r1.Ticks - $r0.Ticks) / $pageSecs, 2) } else { 0 }
    }
}

# --- configure and launch ---------------------------------------------------
Get-Process -Name 'HomeAssistant.Desktop' -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 3

$cfg = Get-Content $settings -Raw | ConvertFrom-Json
$cfg.HomeUrl = if ($Url) { $Url } else { $benchUrl + '?id=B' }
$cfg | Add-Member -NotePropertyName RenderWhenCovered -NotePropertyValue ([bool]$RenderWhenCovered) -Force
$cfg | Add-Member -NotePropertyName ReduceAnimations -NotePropertyValue ([bool]$ReduceAnimations) -Force
$cfg | ConvertTo-Json | Set-Content $settings

Write-Host ("=== RenderWhenCovered = {0} ===" -f [bool]$RenderWhenCovered) -ForegroundColor Cyan

Start-Process $appExe
Start-Sleep -Seconds 22
$appPid = (Get-Process -Name 'HomeAssistant.Desktop').Id
$appHwnd = (Get-Process -Id $appPid).MainWindowHandle
[Win]::MoveWindow($appHwnd, 200, 100, 1400, 900, $true) | Out-Null
# SetForegroundWindow from another process is subject to Windows' foreground lock and
# silently fails, which left the window behind another one and made the "uncovered"
# phase measure a collapsed WebView. Force it to the top instead.
[Win]::SetWindowPos($appHwnd, [Win]::HWND_TOPMOST, 0, 0, 0, 0, ([Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE)) | Out-Null
Start-Sleep -Seconds 8

# confirm the flag really is or is not on the browser process
$cmdline = (Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
    Where-Object { $_.ParentProcessId -eq $appPid }).CommandLine
Write-Host ('  --disable-backgrounding-occluded-windows present: {0}' -f `
    ($cmdline -match 'disable-backgrounding-occluded-windows'))
Write-Host ('  --force-prefers-reduced-motion present:           {0}' -f `
    ($cmdline -match 'force-prefers-reduced-motion'))

# --- visible ----------------------------------------------------------------
$visible = Measure-State -RootPid $appPid -BenchId 'B' -Seconds $Seconds
Write-Host ('  {0,-22} {1,8:N2}% CPU  {2,7:N1} fps  {3,6:N2} ticks/s' -f `
    'uncovered', $visible.Cpu, $visible.Fps, $visible.Ticks)

# --- covered ----------------------------------------------------------------
# The covering window is identified strictly by a title this harness controls.
# No fallback: an earlier version fell back to "the last visible window" and
# maximised one of the user's own Chrome windows.
$coverUrl = 'file:///' + ((Join-Path $here 'cover.html') -replace '\\', '/')
# Drop topmost so the cover window can actually get above it.
[Win]::SetWindowPos($appHwnd, [Win]::HWND_NOTOPMOST, 0, 0, 0, 0, ([Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE)) | Out-Null
Start-Process $chrome -ArgumentList "--app=$coverUrl"
Start-Sleep -Seconds 10
$cover = Get-WindowsMatching -Pattern '^HA-BENCH-COVER' | Select-Object -First 1
if (-not $cover) {
    throw 'The cover window did not appear. Refusing to touch any other window.'
}
[Win]::ShowWindow($cover.Hwnd, $SW_MAXIMIZE) | Out-Null
[Win]::SetForegroundWindow($cover.Hwnd) | Out-Null

# Wait until the cover genuinely holds foreground. Chrome takes a while to settle and
# can hand focus back and forth while it does; measuring through that produced a
# half-collapsed average (83.9 fps) that looked like a slow watcher rather than a
# racy test.
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and [Win]::GetForegroundWindow() -ne $cover.Hwnd) {
    [Win]::SetForegroundWindow($cover.Hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
}
Write-Host ('  cover holds foreground: {0}' -f ([Win]::GetForegroundWindow() -eq $cover.Hwnd))

# Then give the watcher's 2s poll a few cycles to observe the settled state.
Start-Sleep -Seconds 10

$covered = Measure-State -RootPid $appPid -BenchId 'B' -Seconds $Seconds
$fg = [Win]::GetForegroundWindow()
$fgTitle = (Get-WindowsMatching -Pattern '.' | Where-Object { $_.Hwnd -eq $fg } | Select-Object -First 1).Title
Write-Host ('  foreground during covered phase: [{0}]  isApp={1}' -f $fgTitle, ($fg -eq $appHwnd))
Write-Host ('  {0,-22} {1,8:N2}% CPU  {2,7:N1} fps  {3,6:N2} ticks/s' -f `
    'fully covered', $covered.Cpu, $covered.Fps, $covered.Ticks)

[Win]::PostMessage($cover.Hwnd, [Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null

Write-Host ''
Write-Host ('  CPU while covered: {0:N2}% -> {1:N2}%  ({2:N0}% change)' -f `
    $visible.Cpu, $covered.Cpu, ((($covered.Cpu / [math]::Max($visible.Cpu, 0.01)) - 1) * 100))
Write-Host ('  timer fidelity while covered: {0:N2} of {1:N2} ticks/s' -f $covered.Ticks, $visible.Ticks)
