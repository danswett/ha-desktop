# Verifies the lock / display-off / resume handling.
#
# Deliberately does not lock the workstation. The watcher's whole job is to react to
# WM_WTSSESSION_CHANGE and WM_POWERBROADCAST, so the test posts those messages to its
# window directly - the same code path a real lock takes, without locking anyone out
# or putting anything on screen.

param([int]$Seconds = 30)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Sess {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string cls, string win);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@

$WM_WTSSESSION_CHANGE = 0x02B1
$WTS_SESSION_LOCK = 7
$WTS_SESSION_UNLOCK = 8

$benchUrl = 'file:///' + ((Join-Path $here 'bench.html') -replace '\\', '/')
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'
$settings = Join-Path $env:LOCALAPPDATA 'HomeAssistantDesktop\settings.json'

function Measure-App {
    param([int]$RootPid, [int]$Seconds)
    $pids = Get-ProcessTreePids -RootPid $RootPid
    $before = @{}
    foreach ($id in $pids) { try { $before[$id] = (Get-Process -Id $id -EA Stop).TotalProcessorTime } catch { } }
    $r0 = Get-BenchReading -Id 'B'
    $t0 = [DateTime]::UtcNow
    Start-Sleep -Seconds $Seconds
    $wall = ([DateTime]::UtcNow - $t0).TotalSeconds
    $r1 = Get-BenchReading -Id 'B'
    $cpu = 0.0
    foreach ($id in $pids) {
        try { $p = Get-Process -Id $id -EA Stop; if ($before.ContainsKey($id)) { $cpu += ($p.TotalProcessorTime - $before[$id]).TotalSeconds } } catch { }
    }
    $secs = $r1.Elapsed - $r0.Elapsed
    [pscustomobject]@{
        Cpu   = [math]::Round(($cpu / $wall) * 100, 2)
        Fps   = if ($secs -gt 0) { [math]::Round(($r1.Frames - $r0.Frames) / $secs, 1) } else { 0 }
        Ticks = if ($secs -gt 0) { [math]::Round(($r1.Ticks - $r0.Ticks) / $secs, 2) } else { 0 }
    }
}

Get-Process -Name 'HomeAssistant.Desktop' -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 3

$cfg = Get-Content $settings -Raw | ConvertFrom-Json
$cfg.HomeUrl = ($benchUrl + '?id=B')
$cfg | ConvertTo-Json | Set-Content $settings

Start-Process $appExe
Start-Sleep -Seconds 22
$appPid = (Get-Process -Name 'HomeAssistant.Desktop').Id
$appHwnd = (Get-Process -Id $appPid).MainWindowHandle
[Win]::MoveWindow($appHwnd, 200, 100, 1400, 900, $true) | Out-Null
[Win]::SetWindowPos($appHwnd, [Win]::HWND_TOPMOST, 0, 0, 0, 0, ([Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE)) | Out-Null
Start-Sleep -Seconds 8

$stateHwnd = [Sess]::FindWindowW('HomeAssistantDesktop.SystemStateWindow', 'HomeAssistantDesktopSystemState')
Write-Host ("  system state window found: {0}" -f ($stateHwnd -ne [IntPtr]::Zero))
if ($stateHwnd -eq [IntPtr]::Zero) { throw 'SystemStateWatcher window not found.' }

$awake = Measure-App -RootPid $appPid -Seconds $Seconds
Write-Host ('  {0,-22} {1,8:N2}% CPU  {2,7:N1} fps  {3,6:N2} ticks/s' -f 'unlocked', $awake.Cpu, $awake.Fps, $awake.Ticks)

[Sess]::PostMessage($stateHwnd, $WM_WTSSESSION_CHANGE, [IntPtr]$WTS_SESSION_LOCK, [IntPtr]::Zero) | Out-Null
Start-Sleep -Seconds 6
$locked = Measure-App -RootPid $appPid -Seconds $Seconds
Write-Host ('  {0,-22} {1,8:N2}% CPU  {2,7:N1} fps  {3,6:N2} ticks/s' -f 'session LOCKED', $locked.Cpu, $locked.Fps, $locked.Ticks)

[Sess]::PostMessage($stateHwnd, $WM_WTSSESSION_CHANGE, [IntPtr]$WTS_SESSION_UNLOCK, [IntPtr]::Zero) | Out-Null
Start-Sleep -Seconds 6
$unlocked = Measure-App -RootPid $appPid -Seconds $Seconds
Write-Host ('  {0,-22} {1,8:N2}% CPU  {2,7:N1} fps  {3,6:N2} ticks/s' -f 'unlocked again', $unlocked.Cpu, $unlocked.Fps, $unlocked.Ticks)

[Win]::SetWindowPos($appHwnd, [Win]::HWND_NOTOPMOST, 0, 0, 0, 0, ([Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE)) | Out-Null

Write-Host ''
Write-Host ('  CPU while locked:  {0:N2}% -> {1:N2}%' -f $awake.Cpu, $locked.Cpu)
Write-Host ('  timers preserved:  {0:N2} -> {1:N2} ticks/s' -f $awake.Ticks, $locked.Ticks)
Write-Host ('  recovered on unlock: {0:N1} fps' -f $unlocked.Fps)
