# Background throttling comparison.
#
# Chromium clamps timers in a page it believes nobody is looking at: ~1Hz once
# hidden, and 1/min after it has been hidden a while. For a browser that is the
# right trade. For a dashboard that lives in the tray and is supposed to be current
# the moment you glance at it, it is the wrong one - which is why the WinUI app
# creates its WebView2 environment with background timer throttling, renderer
# backgrounding and occluded-window backgrounding all disabled.
#
# This measures that directly. The same page runs in three hosts; all three are
# hidden for a fixed period and their timer counters are sampled throughout.
# Window titles stay readable while a window is minimised or parked in the tray,
# so nothing has to be restored to take a reading.
#
#   A  Chrome app window in the user's existing Chrome   (stock Chrome behaviour)
#   C  Chrome app window in an isolated Chrome           (stock, no other tabs)
#   B  HomeAssistant.Desktop                             (throttling disabled)

param(
    [int]$VisibleSeconds = 20,
    [int]$HiddenSeconds = 120
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$benchUrl = 'file:///' + ((Join-Path $here 'bench.html') -replace '\\', '/')
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$cleanProfile = Join-Path $env:TEMP 'ha-bench-chrome-profile'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

$SW_MINIMIZE = 6
$SW_RESTORE = 9
$TICK_HZ = 4.0   # the page runs a 250ms interval

Write-Host '=== setup ===' -ForegroundColor Cyan

Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2

$cfg = Get-Content $settings -Raw | ConvertFrom-Json
$cfg.HomeUrl = ($benchUrl + '?id=B')
$cfg.MinimizeToTray = $true
$cfg.PSObject.Properties.Remove('Placement')
$cfg | ConvertTo-Json | Set-Content $settings

Start-Process $appExe
Start-Process $chrome -ArgumentList ("--app=" + $benchUrl + '?id=A')
if (Test-Path $cleanProfile) { Remove-Item $cleanProfile -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process $chrome -ArgumentList @(
    "--user-data-dir=$cleanProfile", '--no-first-run', '--no-default-browser-check',
    ("--app=" + $benchUrl + '?id=C')
)
Start-Sleep -Seconds 20

$ids = 'A', 'C', 'B'
$labels = @{ A = 'A Chrome (your Chrome)'; C = 'C Chrome (isolated)'; B = 'B WinUI app' }

$windows = @{}
foreach ($id in $ids) {
    $windows[$id] = Get-WindowsMatching -Pattern "^BENCH $id " | Select-Object -First 1
    if (-not $windows[$id]) { throw "No BENCH $id window appeared." }
    [Win]::MoveWindow($windows[$id].Hwnd, 40 + (740 * $ids.IndexOf($id)), 60, 700, 520, $true) | Out-Null
}
Write-Host '  all three windows placed and visible'
Start-Sleep -Seconds 5

# ---- visible baseline ------------------------------------------------------
Write-Host ''
Write-Host "=== visible baseline ($VisibleSeconds s) ===" -ForegroundColor Cyan
$v0 = @{}; foreach ($id in $ids) { $v0[$id] = Get-BenchReading -Id $id }
Start-Sleep -Seconds $VisibleSeconds
$v1 = @{}; foreach ($id in $ids) { $v1[$id] = Get-BenchReading -Id $id }

$baseline = @{}
foreach ($id in $ids) {
    $secs = $v1[$id].Elapsed - $v0[$id].Elapsed
    $rate = ($v1[$id].Ticks - $v0[$id].Ticks) / $secs
    $fps = ($v1[$id].Frames - $v0[$id].Frames) / $secs
    $baseline[$id] = $rate
    Write-Host ("  {0,-24} {1,6:N2} ticks/s   {2,6:N1} fps" -f $labels[$id], $rate, $fps)
}

# ---- hide everything -------------------------------------------------------
Write-Host ''
Write-Host "=== hidden for $HiddenSeconds s ===" -ForegroundColor Cyan
foreach ($id in $ids) {
    [Win]::ShowWindow($windows[$id].Hwnd, $SW_MINIMIZE) | Out-Null
}
Start-Sleep -Seconds 4
foreach ($id in $ids) {
    $w = Get-WindowsMatching -Pattern "^BENCH $id " | Select-Object -First 1
    Write-Host ("  {0,-24} visible={1}" -f $labels[$id], $w.Visible)
}

$h0 = @{}; foreach ($id in $ids) { $h0[$id] = Get-BenchReading -Id $id }

$elapsed = 0
$step = 30
while ($elapsed -lt $HiddenSeconds) {
    Start-Sleep -Seconds $step
    $elapsed += $step
    $line = "  t+{0,3}s " -f $elapsed
    foreach ($id in $ids) {
        $r = Get-BenchReading -Id $id
        $secs = $r.Elapsed - $h0[$id].Elapsed
        $rate = if ($secs -gt 0) { ($r.Ticks - $h0[$id].Ticks) / $secs } else { 0 }
        $line += ("| {0} {1,5:N2}/s " -f $id, $rate)
    }
    Write-Host $line
}

$h1 = @{}; foreach ($id in $ids) { $h1[$id] = Get-BenchReading -Id $id }

# ---- restore ---------------------------------------------------------------
foreach ($id in $ids) {
    [Win]::ShowWindow($windows[$id].Hwnd, $SW_RESTORE) | Out-Null
}
Start-Sleep -Seconds 3

Write-Host ''
Write-Host '=== result: timer fidelity while hidden ===' -ForegroundColor Cyan
Write-Host ('  {0,-24} {1,10} {2,10} {3,10} {4,12}' -f 'host', 'visible', 'hidden', 'retained', 'ticks lost')
foreach ($id in $ids) {
    $secs = $h1[$id].Elapsed - $h0[$id].Elapsed
    $ticks = $h1[$id].Ticks - $h0[$id].Ticks
    $rate = if ($secs -gt 0) { $ticks / $secs } else { 0 }
    $expected = $secs * $TICK_HZ
    $retained = if ($expected -gt 0) { ($ticks / $expected) * 100 } else { 0 }
    Write-Host ("  {0,-24} {1,8:N2}/s {2,8:N2}/s {3,8:N0}% {4,10:N0}" -f `
        $labels[$id], $baseline[$id], $rate, $retained, ($expected - $ticks))
}
Write-Host ''
Write-Host ('  (page runs a 250ms interval, so {0:N0} ticks/s is full fidelity)' -f $TICK_HZ)

# Close everything this script opened. Leaving a bench window animating at the
# display refresh rate inside the user's Chrome quietly inflates any CPU figure
# measured afterwards.
Write-Host ''
Write-Host '=== cleanup ===' -ForegroundColor Cyan
foreach ($t in (Get-WindowsMatching -Pattern '^BENCH ')) {
    [Win]::PostMessage($t.Hwnd, [Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
Start-Sleep -Seconds 4
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
    Where-Object { $_.CommandLine -match 'ha-bench-chrome-profile' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Write-Host ('  bench windows remaining: {0}' -f (@(Get-WindowsMatching -Pattern '^BENCH ')).Count)
Write-Host '  NOTE: the app is left stopped and its HomeUrl still points at bench.html.'

