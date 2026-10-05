# Engine overhead on identical content.
#
# The dashboard comparison kept getting confounded - each host restores a different
# view, Chrome reuses an existing renderer when the same site is already open, and a
# covered window stops doing work in Chrome but not in this app (by design). So this
# strips all of that out: the same trivial local page, the same window size, each
# host measured on its own with its window in the foreground and nothing else of
# the test running.
#
# If the app needs far more CPU than Chrome for this page, the cost is the host.
# If they are close, the earlier gap was the content, not the engine.

param(
    [int]$Seconds = 45,
    [int]$WinWidth = 1400,
    [int]$WinHeight = 900,
    # A brand-new Chrome profile spends its first run building shader and GPU caches
    # and renders well below the display refresh rate while it does, which makes its
    # CPU look flatteringly low. Reuse a warm profile unless asked otherwise.
    [switch]$FreshProfile
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$benchUrl = 'file:///' + ((Join-Path $here 'bench.html') -replace '\\', '/')
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$cleanProfile = Join-Path $env:TEMP 'ha-bench-chrome-profile'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

function Measure-TreeRate {
    param([int]$RootPid, [int]$Seconds)
    $pids = Get-ProcessTreePids -RootPid $RootPid
    $before = @{}
    foreach ($id in $pids) { try { $before[$id] = (Get-Process -Id $id -EA Stop).TotalProcessorTime } catch { } }
    $t0 = [DateTime]::UtcNow
    Start-Sleep -Seconds $Seconds
    $wall = ([DateTime]::UtcNow - $t0).TotalSeconds

    $cpuSeconds = 0.0
    $ws = 0.0
    $rows = @()
    foreach ($id in $pids) {
        try {
            $p = Get-Process -Id $id -EA Stop
            $ws += $p.WorkingSet64 / 1MB
            if ($before.ContainsKey($id)) {
                $d = ($p.TotalProcessorTime - $before[$id]).TotalSeconds
                $cpuSeconds += $d
                $ci = (Get-CimInstance Win32_Process -Filter "ProcessId=$id" -EA SilentlyContinue).CommandLine
                $type = if ($ci -match '--type=([\w-]+)') { $Matches[1] } else { 'main' }
                $rows += [pscustomobject]@{ Pid = $id; Type = $type; Pct = [math]::Round(($d / $wall) * 100, 2) }
            }
        }
        catch { }
    }

    [pscustomobject]@{
        CpuPercent = [math]::Round(($cpuSeconds / $wall) * 100, 2)
        WorkingSet = [math]::Round($ws, 0)
        Processes  = $pids.Count
        Rows       = ($rows | Sort-Object Pct -Descending)
    }
}

function Stop-All {
    Get-Process -Name 'HomeAssistant.Desktop' -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
        Where-Object { $_.CommandLine -match 'ha-bench-chrome-profile' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
    Start-Sleep -Seconds 4
}

Stop-All

# ===========================================================================
Write-Host '=== WinUI app on bench.html (alone, foreground) ===' -ForegroundColor Cyan
$cfg = Get-Content $settings -Raw | ConvertFrom-Json
$cfg.HomeUrl = ($benchUrl + '?id=B')
$cfg | ConvertTo-Json | Set-Content $settings
Start-Process $appExe
Start-Sleep -Seconds 25
$appPid = (Get-Process -Name 'HomeAssistant.Desktop').Id
$appHwnd = (Get-Process -Id $appPid).MainWindowHandle
[Win]::MoveWindow($appHwnd, 200, 100, $WinWidth, $WinHeight, $true) | Out-Null
[Win]::SetForegroundWindow($appHwnd) | Out-Null
Start-Sleep -Seconds 6
$r0 = Get-BenchReading -Id 'B'
$app = Measure-TreeRate -RootPid $appPid -Seconds $Seconds
$r1 = Get-BenchReading -Id 'B'
$appFps = [math]::Round(($r1.Frames - $r0.Frames) / ($r1.Elapsed - $r0.Elapsed), 1)

Write-Host ('  total {0,7:N2}% CPU   {1,6:N0} MB   {2} procs   {3} fps' -f `
    $app.CpuPercent, $app.WorkingSet, $app.Processes, $appFps)
foreach ($row in $app.Rows) { Write-Host ('    {0,-18} pid {1,-7} {2,7:N2}%' -f $row.Type, $row.Pid, $row.Pct) }

Stop-All

# ===========================================================================
Write-Host ''
Write-Host '=== isolated Chrome on bench.html (alone, foreground) ===' -ForegroundColor Cyan
if ($FreshProfile -and (Test-Path $cleanProfile)) { Remove-Item $cleanProfile -Recurse -Force -EA SilentlyContinue }
Start-Process $chrome -ArgumentList @(
    "--user-data-dir=$cleanProfile", '--no-first-run', '--no-default-browser-check',
    ("--app=" + $benchUrl + '?id=C')
)
Start-Sleep -Seconds 25
$chromeRoot = (Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
    Where-Object { $_.CommandLine -match 'ha-bench-chrome-profile' -and $_.CommandLine -notmatch '--type=' } |
    Select-Object -First 1).ProcessId
$cw = Get-WindowsMatching -Pattern '^BENCH C ' | Select-Object -First 1
[Win]::MoveWindow($cw.Hwnd, 200, 100, $WinWidth, $WinHeight, $true) | Out-Null
[Win]::SetForegroundWindow($cw.Hwnd) | Out-Null
Start-Sleep -Seconds 6
$c0 = Get-BenchReading -Id 'C'
$ch = Measure-TreeRate -RootPid $chromeRoot -Seconds $Seconds
$c1 = Get-BenchReading -Id 'C'
$chFps = [math]::Round(($c1.Frames - $c0.Frames) / ($c1.Elapsed - $c0.Elapsed), 1)

Write-Host ('  total {0,7:N2}% CPU   {1,6:N0} MB   {2} procs   {3} fps' -f `
    $ch.CpuPercent, $ch.WorkingSet, $ch.Processes, $chFps)
foreach ($row in $ch.Rows) { Write-Host ('    {0,-18} pid {1,-7} {2,7:N2}%' -f $row.Type, $row.Pid, $row.Pct) }

Stop-All

Write-Host ''
Write-Host '=== identical page, identical window, each alone ===' -ForegroundColor Cyan
Write-Host ('  {0,-22} {1,8:N2}%   {2,6:N0} MB   {3,6} fps' -f 'WinUI app', $app.CpuPercent, $app.WorkingSet, $appFps)
Write-Host ('  {0,-22} {1,8:N2}%   {2,6:N0} MB   {3,6} fps' -f 'Chrome (isolated)', $ch.CpuPercent, $ch.WorkingSet, $chFps)
