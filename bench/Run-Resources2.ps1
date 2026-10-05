# Resource cost of one Home Assistant dashboard in each host.
#
# A fresh Chrome profile cannot be used for this: it is not signed in to Home
# Assistant, so it never reaches the dashboard. The dashboard has to be measured
# inside the user's real, authenticated Chrome - which is also the honest case,
# because that is where the installed Chrome app actually runs.
#
# Chrome's cost is therefore measured as a *marginal* cost: the whole Chrome
# process tree is measured, an additional dashboard window is opened, and the tree
# is measured again. Whether Chrome serves it from a new renderer or folds it into
# an existing one does not matter - the delta across the entire tree captures it
# either way. That window is opened by this script and closed again afterwards;
# nothing the user had open is touched.
#
# The WinUI app is standalone, so its whole tree *is* its marginal cost.

param(
    [int]$SettleSeconds = 45,
    [string]$DashboardUrl = 'http://192.168.1.188:8123'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'Measure-Apps.ps1')

$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$appExe = Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop\HomeAssistant.Desktop.exe'

function Get-UserChromePid {
    (Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
        Where-Object { $_.CommandLine -notmatch '--type=' -and $_.CommandLine -notmatch 'ha-bench-chrome-profile' } |
        Select-Object -First 1).ProcessId
}

function Measure-Tree {
    param([int]$RootPid)
    Get-TreeSnapshot -Pids (Get-ProcessTreePids -RootPid $RootPid)
}

function Show-Delta {
    param($Label, $Before, $After, $Seconds)
    $wall = ($After.At - $Before.At).TotalSeconds
    Write-Host ('  {0,-34} {1,7} {2,9} {3,10:N0} MB {4,10:N0} MB' -f `
        $Label,
        ($After.Processes - $Before.Processes),
        ($After.Threads - $Before.Threads),
        (($After.WorkingSet - $Before.WorkingSet) / 1MB),
        (($After.Private - $Before.Private) / 1MB))
}

# --- tidy up anything left from earlier runs --------------------------------
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
    Where-Object { $_.CommandLine -match 'ha-bench-chrome-profile' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3

# ===========================================================================
Write-Host '=== marginal cost of one dashboard inside your Chrome ===' -ForegroundColor Cyan
$userChrome = Get-UserChromePid
Write-Host "  your Chrome browser process: pid $userChrome"

$before = Measure-Tree -RootPid $userChrome
Write-Host ('  before: {0} processes, {1} threads, {2:N0} MB working set, {3:N0} MB private' -f `
    $before.Processes, $before.Threads, ($before.WorkingSet / 1MB), ($before.Private / 1MB))

Start-Process $chrome -ArgumentList "--app=$DashboardUrl"
Start-Sleep -Seconds $SettleSeconds

$after = Measure-Tree -RootPid $userChrome
Write-Host ('  after : {0} processes, {1} threads, {2:N0} MB working set, {3:N0} MB private' -f `
    $after.Processes, $after.Threads, ($after.WorkingSet / 1MB), ($after.Private / 1MB))

# CPU of the whole Chrome tree while the extra dashboard is up
$cpuA = $after
Start-Sleep -Seconds 60
$cpuB = Measure-Tree -RootPid $userChrome
$chromeCpu = (($cpuB.CpuTime - $cpuA.CpuTime).TotalSeconds / ($cpuB.At - $cpuA.At).TotalSeconds) * 100

Write-Host ''
Write-Host ('  {0,-34} {1,7} {2,9} {3,13} {4,13}' -f 'delta attributable to dashboard', 'procs', 'threads', 'workingSet', 'private')
Show-Delta 'Chrome marginal cost' $before $after

# close only the window this script opened
$mine = Get-WindowsMatching -Pattern 'Home Assistant' |
    Where-Object { $_.Pid -eq $userChrome } | Select-Object -Last 1
if ($mine) {
    [Win]::PostMessage($mine.Hwnd, [Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Write-Host '  (closed the extra window this script opened)'
}

# ===========================================================================
Write-Host ''
Write-Host '=== the WinUI app, whole tree ===' -ForegroundColor Cyan
Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 4

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Start-Process $appExe
$appPid = $null
while ($sw.Elapsed.TotalSeconds -lt 60 -and -not $appPid) {
    $appPid = (Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue).Id
    Start-Sleep -Milliseconds 100
}
Start-Sleep -Seconds $SettleSeconds

$appA = Measure-Tree -RootPid $appPid
Start-Sleep -Seconds 60
$appB = Measure-Tree -RootPid $appPid
$appCpu = (($appB.CpuTime - $appA.CpuTime).TotalSeconds / ($appB.At - $appA.At).TotalSeconds) * 100

Write-Host ('  {0,-34} {1,7} {2,9} {3,10:N0} MB {4,10:N0} MB' -f `
    'WinUI app total cost', $appB.Processes, $appB.Threads, ($appB.WorkingSet / 1MB), ($appB.Private / 1MB))

# ===========================================================================
Write-Host ''
Write-Host '=== steady-state CPU, whole tree ===' -ForegroundColor Cyan
Write-Host ('  {0,-34} {1,8:N2}%' -f 'your entire Chrome (with dashboard)', $chromeCpu)
Write-Host ('  {0,-34} {1,8:N2}%' -f 'WinUI app', $appCpu)

Write-Host ''
Write-Host '=== coupling ===' -ForegroundColor Cyan
$gpu = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
    Where-Object { $_.CommandLine -match '--type=gpu-process' -and $_.CommandLine -notmatch 'ha-bench-chrome-profile' }
Write-Host ('  Chrome GPU/compositor processes serving every Chrome window: {0} (pid {1})' -f `
    (@($gpu)).Count, (($gpu | ForEach-Object { $_.ProcessId }) -join ', '))
$ourGpu = Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
    Where-Object { $_.CommandLine -match '--type=gpu-process' -and $_.CommandLine -match 'HomeAssistantDesktop' }
Write-Host ('  the WinUI app''s own GPU process:                             pid {0}' -f `
    (($ourGpu | ForEach-Object { $_.ProcessId }) -join ', '))
Write-Host ('  chrome.exe processes on the machine: {0}' -f (Get-Process -Name chrome -ErrorAction SilentlyContinue).Count)
