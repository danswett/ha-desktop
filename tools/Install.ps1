# Publishes a self-contained build and installs it for the current user.
#
# The app is published self-contained - both .NET and the Windows App SDK - so the
# installed copy keeps working regardless of what happens to machine-wide runtimes.
# The only external dependency is the Microsoft Edge WebView2 Runtime, which ships
# with Windows 11.
#
#   pwsh -File tools\Install.ps1
#   pwsh -File tools\Install.ps1 -StartWithWindows
#   pwsh -File tools\Install.ps1 -InstallRoot D:\Apps\HomeAssistantDesktop

[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\HomeAssistantDesktop'),
    [switch]$StartWithWindows,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\HomeAssistant.Desktop\HomeAssistant.Desktop.csproj'
$staging = Join-Path $repoRoot 'publish'
$exeName = 'HomeAssistant.Desktop.exe'

if (-not (Test-Path $project)) {
    throw "Could not find the project at $project"
}

Write-Host 'Publishing (self-contained, win-x64)...' -ForegroundColor Cyan
if (Test-Path $staging) {
    Remove-Item $staging -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 -o $staging --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $staging $exeName
if (-not (Test-Path $publishedExe)) {
    throw "Publish completed but $exeName was not produced."
}

# A running instance holds its own files open.
Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running instance (pid $($_.Id))..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force
    Start-Sleep -Seconds 2
}

Write-Host "Installing to $InstallRoot..." -ForegroundColor Cyan
if (Test-Path $InstallRoot) {
    Remove-Item $InstallRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item (Join-Path $staging '*') $InstallRoot -Recurse -Force

$installedExe = Join-Path $InstallRoot $exeName

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = Join-Path $startMenu 'Home Assistant.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $installedExe
$link.WorkingDirectory = $InstallRoot
$link.IconLocation = "$installedExe,0"
$link.Description = 'Home Assistant dashboard in its own isolated browser process'
$link.Save()
Write-Host "Start Menu shortcut: $shortcut" -ForegroundColor Green

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($StartWithWindows) {
    New-ItemProperty -Path $runKey -Name 'HomeAssistantDesktop' `
        -Value "`"$installedExe`" --minimized" -PropertyType String -Force | Out-Null
    Write-Host 'Registered to start with Windows (hidden in the tray).' -ForegroundColor Green
}
else {
    # Keep any existing registration pointed at the new location.
    $existing = Get-ItemProperty -Path $runKey -Name 'HomeAssistantDesktop' -ErrorAction SilentlyContinue
    if ($existing) {
        New-ItemProperty -Path $runKey -Name 'HomeAssistantDesktop' `
            -Value "`"$installedExe`" --minimized" -PropertyType String -Force | Out-Null
        Write-Host 'Updated the existing start-with-Windows entry to the new path.' -ForegroundColor Green
    }
}

$size = (Get-ChildItem $InstallRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Installed {0:N0} MB to {1}" -f $size, $InstallRoot) -ForegroundColor Green

if (-not $NoLaunch) {
    Write-Host 'Launching...' -ForegroundColor Cyan
    Start-Process -FilePath $installedExe
}
