# Where the CPU actually goes, visible vs parked in the tray.
#
# Disabling background throttling is the point of the app, but it has an obvious
# risk: a dashboard that never backs off could burn CPU all day while nobody is
# looking at it. This measures per process, in both states, so the cost of that
# decision is visible rather than assumed.

param([int]$Seconds = 45)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

function Get-PerProcessCpu {
    param([int]$RootPid, [int]$Seconds)

    $pids = Get-ProcessTreePids -RootPid $RootPid
    $kind = @{}
    foreach ($p in (Get-CimInstance Win32_Process | Where-Object { $pids -contains [int]$_.ProcessId })) {
        $t = if ($p.CommandLine -match '--type=([\w-]+)') { $Matches[1] } else { 'main' }
        $kind[[int]$p.ProcessId] = "$($p.Name -replace '\.exe$','') / $t"
    }

    $a = @{}
    foreach ($id in $pids) {
        try { $a[$id] = (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime } catch { }
    }
    $t0 = [DateTime]::UtcNow
    Start-Sleep -Seconds $Seconds
    $t1 = [DateTime]::UtcNow
    $wall = ($t1 - $t0).TotalSeconds

    $rows = @()
    foreach ($id in $pids) {
        try {
            $p = Get-Process -Id $id -ErrorAction Stop
            if (-not $a.ContainsKey($id)) { continue }
            $pct = (($p.TotalProcessorTime - $a[$id]).TotalSeconds / $wall) * 100
            $rows += [pscustomobject]@{
                Pid = $id
                Kind = $kind[$id]
                CpuPct = [math]::Round($pct, 2)
                WsMB = [math]::Round($p.WorkingSet64 / 1MB, 0)
            }
        }
        catch { }
    }
    , ($rows | Sort-Object CpuPct -Descending)
}

function Show-Rows {
    param($Rows, $Title)
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Yellow
    $total = 0
    foreach ($r in $Rows) {
        $total += $r.CpuPct
        Write-Host ('    {0,-34} pid {1,-7} {2,7:N2}%  {3,6:N0} MB' -f $r.Kind, $r.Pid, $r.CpuPct, $r.WsMB)
    }
    Write-Host ('    {0,-34} {1,11} {2,7:N2}%' -f 'TOTAL', '', $total)
    return $total
}

$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'

Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 4
Start-Process $appExe
Start-Sleep -Seconds 40
$appPid = (Get-Process -Name 'HomeAssistant.Desktop').Id
Write-Host "=== WinUI app (pid $appPid) ===" -ForegroundColor Cyan

$visible = Get-PerProcessCpu -RootPid $appPid -Seconds $Seconds
$visTotal = Show-Rows $visible "VISIBLE ($Seconds s)"

# park it in the tray, the way it spends most of its life
$hwnd = (Get-Process -Id $appPid).MainWindowHandle
[Win]::ShowWindow($hwnd, 6) | Out-Null   # SW_MINIMIZE -> app hides to tray
Start-Sleep -Seconds 12
Write-Host ''
Write-Host ('  window visible now: {0}' -f [Win]::IsWindowVisible($hwnd))

$tray = Get-PerProcessCpu -RootPid $appPid -Seconds $Seconds
$trayTotal = Show-Rows $tray "IN TRAY ($Seconds s)"

Write-Host ''
Write-Host '=== summary ===' -ForegroundColor Cyan
Write-Host ('  visible {0,7:N2}%   in tray {1,7:N2}%   drop {2,6:N0}%' -f `
    $visTotal, $trayTotal, (100 - (($trayTotal / [math]::Max($visTotal, 0.01)) * 100)))

[Win]::ShowWindow($hwnd, 9) | Out-Null
