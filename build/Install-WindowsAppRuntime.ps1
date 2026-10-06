<#
.SYNOPSIS
    Installs the Windows App Runtime, so a build machine can supply
    Microsoft.WindowsAppRuntime.Insights.Resource.dll.

.DESCRIPTION
    The app ships self-contained and needs no runtime to *run*. It does need one to
    *build*, for a single file: Windows App SDK's self-contained output omits
    Microsoft.WindowsAppRuntime.Insights.Resource.dll, and no NuGet package in the
    dependency graph carries it (WindowsAppSDK#6774). Without it,
    AppNotificationManager.Register() fails and the app silently loses every toast.

    tools/Copy-InsightsResource.ps1 takes the file from the installed framework package,
    so a hosted CI runner - which has no Windows App Runtime at all - has to install one
    before building anything it intends to ship.

.PARAMETER Version
    Windows App SDK feature band to install, e.g. '2.5'. Should match the
    WindowsAppSdkPackageVersion in the project.

.EXAMPLE
    pwsh -File build\Install-WindowsAppRuntime.ps1 -Version 2.5
#>
[CmdletBinding()]
param(
    [string]$Version = '2.5'
)

$ErrorActionPreference = 'Stop'
$fileName = 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'

function Test-RuntimePresent {
    $packages = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Architecture -eq 'X64' })

    foreach ($package in $packages) {
        if (Test-Path -LiteralPath (Join-Path $package.InstallLocation $fileName)) {
            return $package
        }
    }

    return $null
}

if ($existing = Test-RuntimePresent) {
    Write-Host "Windows App Runtime $($existing.Version) already provides $fileName."
    exit 0
}

$url = "https://aka.ms/windowsappsdk/$Version/latest/windowsappruntimeinstall-x64.exe"
$installer = Join-Path $env:TEMP 'windowsappruntimeinstall-x64.exe'

Write-Host "Downloading the Windows App Runtime $Version from $url"
Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing

Write-Host 'Installing...'
$process = Start-Process -FilePath $installer -ArgumentList '--quiet' -Wait -PassThru

# 0 is success; 0x80073D06 is "a higher version is already installed", which is fine.
if ($process.ExitCode -ne 0 -and $process.ExitCode -ne -2147009274) {
    throw "The Windows App Runtime installer exited with $($process.ExitCode)."
}

Remove-Item $installer -ErrorAction SilentlyContinue

$installed = Test-RuntimePresent
if (-not $installed) {
    throw "The Windows App Runtime was installed but $fileName is still not present. Toast notifications would be broken in anything built here."
}

Write-Host "Windows App Runtime $($installed.Version) provides $fileName." -ForegroundColor Green
