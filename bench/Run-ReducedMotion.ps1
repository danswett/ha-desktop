# Effect of reduced motion on the real dashboard.
#
# Home Assistant animates continuously - the energy flow card in particular - and
# every one of those frames is rasterised and composited at the display refresh
# rate. prefers-reduced-motion is the standard way to ask a page to stop, and
# Chromium can force it regardless of the OS setting.
#
# Same URL, same window size, foreground, one configuration after the other.

param(
    [int]$Seconds = 45,
    [string]$Url = 'http://192.168.1.188:8123/lovelace/0'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

function Measure-Config {
    param([bool]$ReduceAnimations, [int]$Seconds)

    Get-Process -Name 'HomeAssistant.Desktop' -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 4

    $cfg = Get-Content $settings -Raw | ConvertFrom-Json
    $cfg.HomeUrl = $Url
    $cfg | Add-Member -NotePropertyName ReduceAnimations -NotePropertyValue $ReduceAnimations -Force
    $cfg | Add-Member -NotePropertyName RenderWhenCovered -NotePropertyValue $false -Force
    $cfg | ConvertTo-Json | Set-Content $settings

    Start-Process $appExe
    Start-Sleep -Seconds 40
    $appPid = (Get-Process -Name 'HomeAssistant.Desktop').Id
    $hwnd = (Get-Process -Id $appPid).MainWindowHandle
    [Win]::MoveWindow($hwnd, 200, 100, 1400, 900, $true) | Out-Null
    [Win]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds 10

    $title = (Get-Process -Id $appPid).MainWindowTitle
    $pids = Get-ProcessTreePids -RootPid $appPid
    $before = @{}
    foreach ($id in $pids) { try { $before[$id] = (Get-Process -Id $id -EA Stop).TotalProcessorTime } catch { } }
    $t0 = [DateTime]::UtcNow
    Start-Sleep -Seconds $Seconds
    $wall = ([DateTime]::UtcNow - $t0).TotalSeconds

    $cpu = 0.0; $ws = 0.0
    $rows = @()
    foreach ($id in $pids) {
        try {
            $p = Get-Process -Id $id -EA Stop
            $ws += $p.WorkingSet64 / 1MB
            if ($before.ContainsKey($id)) {
                $d = ($p.TotalProcessorTime - $before[$id]).TotalSeconds
                $cpu += $d
                $ci = (Get-CimInstance Win32_Process -Filter "ProcessId=$id" -EA SilentlyContinue).CommandLine
                $type = if ($ci -match '--type=([\w-]+)') { $Matches[1] } else { 'main' }
                $rows += [pscustomobject]@{ Type = $type; Pct = [math]::Round(($d / $wall) * 100, 2) }
            }
        }
        catch { }
    }

    [pscustomobject]@{
        Title = $title
        Cpu   = [math]::Round(($cpu / $wall) * 100, 2)
        Mem   = [math]::Round($ws, 0)
        Rows  = ($rows | Sort-Object Pct -Descending | Select-Object -First 4)
    }
}

Write-Host '=== real dashboard, 1400x900, foreground ===' -ForegroundColor Cyan

$off = Measure-Config -ReduceAnimations $false -Seconds $Seconds
Write-Host ("  animations ON   {0,8:N2}% CPU  {1,6:N0} MB   [{2}]" -f $off.Cpu, $off.Mem, $off.Title)
foreach ($r in $off.Rows) { Write-Host ('      {0,-16} {1,7:N2}%' -f $r.Type, $r.Pct) }

$on = Measure-Config -ReduceAnimations $true -Seconds $Seconds
Write-Host ("  reduced motion  {0,8:N2}% CPU  {1,6:N0} MB   [{2}]" -f $on.Cpu, $on.Mem, $on.Title)
foreach ($r in $on.Rows) { Write-Host ('      {0,-16} {1,7:N2}%' -f $r.Type, $r.Pct) }

Write-Host ''
if ($off.Title -ne $on.Title) {
    Write-Host '  WARNING: different views, not comparable.' -ForegroundColor Red
}
Write-Host ('  change: {0:N0}% CPU' -f ((($on.Cpu / [math]::Max($off.Cpu, 0.01)) - 1) * 100))
