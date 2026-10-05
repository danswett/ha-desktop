# Contention comparison.
#
# Runs the identical bench page in three hosts and measures frame health in each:
#
#   A  Chrome app-mode window inside the user's existing Chrome
#      -> shares that browser's UI thread, GPU/compositor process and network service,
#         which is what the installed Home Assistant Chrome app also does
#   C  Chrome app-mode window in a throwaway --user-data-dir
#      -> a clean Chromium baseline with no other tabs competing
#   B  HomeAssistant.Desktop (WebView2, private user-data folder)
#
# B vs C isolates engine overhead. A vs C isolates the cost of sharing a browser.
#
# Phase 1 measures all three at rest. Phase 2 applies a heavy GPU/raster load
# *inside the user's Chrome only*, and measures all three again. Only hosts that
# share resources with that Chrome should degrade.
#
# All windows stay visible and are the same size; occluding one would stop its
# frame callbacks and invalidate the comparison.

param(
    [int]$PhaseSeconds = 30
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$benchUrl = 'file:///' + ((Join-Path $here 'bench.html') -replace '\\', '/')
$loadUrl = 'file:///' + ((Join-Path $here 'load.html') -replace '\\', '/')
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$cleanProfile = Join-Path $env:TEMP 'ha-bench-chrome-profile'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

$benchW = 700; $benchH = 560; $benchY = 60
$posA = 40; $posC = 780; $posB = 1520

function Show-Reading {
    param($Label, $Start, $End)
    if (-not $Start -or -not $End) {
        Write-Host ("  {0,-26} NO READING" -f $Label) -ForegroundColor Red
        return $null
    }
    $secs = $End.Elapsed - $Start.Elapsed
    if ($secs -le 0) { $secs = 1 }
    $frames = $End.Frames - $Start.Frames
    $janky = $End.Janky - $Start.Janky
    $ticks = $End.Ticks - $Start.Ticks
    $fps = [math]::Round($frames / $secs, 1)
    $tps = [math]::Round($ticks / $secs, 2)
    Write-Host ("  {0,-26} {1,6} fps   janky {2,5}   worstFrame {3,5} ms   timer {4,5}/s  worstDrift {5,5} ms" -f `
        $Label, $fps, $janky, $End.WorstGap, $tps, $End.Drift)
    [pscustomobject]@{
        Label = $Label; Fps = $fps; Janky = $janky; WorstGap = $End.WorstGap
        TicksPerSec = $tps; WorstDrift = $End.Drift; Seconds = [math]::Round($secs, 1)
    }
}

Write-Host '=== setup ===' -ForegroundColor Cyan

# --- our app, pointed at the bench page -------------------------------------
Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2

$cfg = Get-Content $settings -Raw | ConvertFrom-Json
$cfg.HomeUrl = ($benchUrl + '?id=B')
$cfg.PSObject.Properties.Remove('Placement')
$cfg | ConvertTo-Json | Set-Content $settings
Start-Process $appExe
Write-Host '  launched HomeAssistant.Desktop (B)'

# --- Chrome app window inside the user's existing Chrome --------------------
Start-Process $chrome -ArgumentList ("--app=" + $benchUrl + '?id=A')
Write-Host '  launched Chrome app window in the existing Chrome (A)'

# --- Chrome app window in a clean, isolated Chrome --------------------------
if (Test-Path $cleanProfile) { Remove-Item $cleanProfile -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process $chrome -ArgumentList @(
    "--user-data-dir=$cleanProfile"
    '--no-first-run'
    '--no-default-browser-check'
    ("--app=" + $benchUrl + '?id=C')
)
Write-Host '  launched isolated Chrome (C)'

Start-Sleep -Seconds 18

# --- place all three, same size, no overlap ---------------------------------
foreach ($pair in @(@('A', $posA), @('C', $posC), @('B', $posB))) {
    $target = Get-WindowsMatching -Pattern "^BENCH $($pair[0]) " | Select-Object -First 1
    if ($target) {
        [Win]::MoveWindow($target.Hwnd, [int]$pair[1], $benchY, $benchW, $benchH, $true) | Out-Null
        Write-Host ("  placed {0} at x={1}" -f $pair[0], $pair[1])
    }
    else {
        Write-Host ("  WARNING: no window for {0}" -f $pair[0]) -ForegroundColor Red
    }
}

Start-Sleep -Seconds 6

Write-Host ''
Write-Host "=== phase 1: at rest ($PhaseSeconds s) ===" -ForegroundColor Cyan
$s1 = @{}
foreach ($id in 'A', 'C', 'B') { $s1[$id] = Get-BenchReading -Id $id }
Start-Sleep -Seconds $PhaseSeconds
$e1 = @{}
foreach ($id in 'A', 'C', 'B') { $e1[$id] = Get-BenchReading -Id $id }

$p1 = @()
$p1 += Show-Reading 'A Chrome (shared)' $s1['A'] $e1['A']
$p1 += Show-Reading 'C Chrome (isolated)' $s1['C'] $e1['C']
$p1 += Show-Reading 'B WinUI app' $s1['B'] $e1['B']

Write-Host ''
Write-Host '=== applying load inside the user''s Chrome ===' -ForegroundColor Yellow
$loadWindows = @()
foreach ($x in 40, 1060, 2080) {
    Start-Process $chrome -ArgumentList "--app=$loadUrl"
    Start-Sleep -Seconds 3
}
Start-Sleep -Seconds 5
# move the load windows below the bench row so nothing is occluded
$i = 0
foreach ($target in (Get-WindowsMatching -Pattern '^LOAD')) {
    $x = @(40, 1060, 2080)[$i % 3]
    [Win]::MoveWindow($target.Hwnd, $x, 700, 980, 900, $true) | Out-Null
    $loadWindows += $target
    $i++
}
Write-Host ("  {0} load windows running" -f $loadWindows.Count)
Start-Sleep -Seconds 8

Write-Host ''
Write-Host "=== phase 2: under load ($PhaseSeconds s) ===" -ForegroundColor Cyan
$s2 = @{}
foreach ($id in 'A', 'C', 'B') { $s2[$id] = Get-BenchReading -Id $id }
Start-Sleep -Seconds $PhaseSeconds
$e2 = @{}
foreach ($id in 'A', 'C', 'B') { $e2[$id] = Get-BenchReading -Id $id }

$p2 = @()
$p2 += Show-Reading 'A Chrome (shared)' $s2['A'] $e2['A']
$p2 += Show-Reading 'C Chrome (isolated)' $s2['C'] $e2['C']
$p2 += Show-Reading 'B WinUI app' $s2['B'] $e2['B']

Write-Host ''
Write-Host '=== degradation under load (phase 2 vs phase 1) ===' -ForegroundColor Cyan
for ($k = 0; $k -lt 3; $k++) {
    if ($p1[$k] -and $p2[$k]) {
        $drop = if ($p1[$k].Fps -gt 0) { [math]::Round((1 - ($p2[$k].Fps / $p1[$k].Fps)) * 100, 1) } else { 0 }
        Write-Host ("  {0,-26} {1,6} -> {2,-6} fps   {3,6}% change   janky {4} -> {5}" -f `
            $p1[$k].Label, $p1[$k].Fps, $p2[$k].Fps, (-$drop), $p1[$k].Janky, $p2[$k].Janky)
    }
}

Write-Host ''
Write-Host '=== cleanup ===' -ForegroundColor Cyan
foreach ($target in (Get-WindowsMatching -Pattern '^LOAD')) {
    [Win]::PostMessage($target.Hwnd, [Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
Start-Sleep -Seconds 3
Write-Host ('  load windows remaining: {0}' -f (@(Get-WindowsMatching -Pattern '^LOAD')).Count)
