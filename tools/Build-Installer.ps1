<#
.SYNOPSIS
    Builds a per-user MSI for Home Assistant Desktop.

.DESCRIPTION
    Publishes the app self-contained and wraps it in an MSI that installs to
    %LOCALAPPDATA%\Programs\HomeAssistantDesktop with a Start Menu shortcut.

    Per-user, so installing it needs no administrator. The app is self-contained, keeps
    its settings and WebView2 profile in the user's profile, and starts from HKCU, so
    there is nothing about it that belongs to the machine.

    Needs the WiX 5 CLI:

        dotnet tool install --global wix --version 5.0.2 --configfile nuget.config

    WiX 6 and 7 require accepting the Open Source Maintenance Fee EULA; WiX 5 is the
    last version under the plain MS-RL.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER OutputDirectory
    Where to put the .msi. Defaults to dist\ at the repo root.

.PARAMETER SkipPublish
    Reuse an existing publish folder instead of rebuilding it.

.PARAMETER AllowMissingInsightsResource
    Build even if Microsoft.WindowsAppRuntime.Insights.Resource.dll is absent. The
    resulting MSI installs an app that cannot raise a single toast, so this exists only
    for building on a machine where notifications are not wanted.

.PARAMETER NuGetConfig
    A NuGet config to restore with. The repo default points at the Microsoft package
    proxy, which a hosted CI runner cannot reach; CI passes build/nuget.ci.config.

.EXAMPLE
    pwsh -File tools\Build-Installer.ps1

.EXAMPLE
    # On the target machine:
    msiexec /i HomeAssistantDesktop-1.0.0-x64.msi /qb STARTWITHWINDOWS=1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$SkipPublish,
    [switch]$AllowMissingInsightsResource,
    [string]$NuGetConfig
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\HomeAssistant.Desktop\HomeAssistant.Desktop.csproj'
$publishDir = Join-Path $repoRoot 'publish'
$wxs = Join-Path $PSScriptRoot 'installer\HomeAssistantDesktop.wxs'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'dist'
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw @'
The WiX CLI was not found. Install it with:

    dotnet tool install --global wix --version 5.0.2 --configfile nuget.config

Use version 5: WiX 6 and later require accepting the Open Source Maintenance Fee EULA.
'@
}

$wixVersion = (wix --version) -replace '\+.*', ''
if ([version]($wixVersion -replace '[^0-9.].*', '') -ge [version]'6.0') {
    Write-Warning "WiX $wixVersion is installed. Versions 6 and later require accepting the Open Source Maintenance Fee EULA."
}

# The MSI version comes from the project, so the two can never drift apart.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Could not read <Version> from $project." }
$version = "$version".Trim()

Write-Host "Home Assistant Desktop $version" -ForegroundColor Cyan

if (-not $SkipPublish) {
    Write-Host 'Publishing (self-contained, win-x64)...'
    $publishArgs = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $publishDir,
        '--nologo'
    )
    if ($NuGetConfig) { $publishArgs += @('--configfile', $NuGetConfig) }

    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}

if (-not (Test-Path (Join-Path $publishDir 'HomeAssistant.Desktop.exe'))) {
    throw "No published app at $publishDir. Run without -SkipPublish."
}

# Without this the app installs fine and then cannot raise a single toast. Fail rather
# than warn: this script produces something meant to be handed to other machines, and
# the failure it would otherwise ship is completely silent.
$insights = Join-Path $publishDir 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
if (-not (Test-Path $insights)) {
    $message = @'
Microsoft.WindowsAppRuntime.Insights.Resource.dll is missing from the publish output.

An MSI built without it installs an app whose toast notifications fail silently.
The file comes from the installed Windows App Runtime framework package, which this
machine appears not to have. Install it with:

    pwsh -File build\Install-WindowsAppRuntime.ps1

See tools/Copy-InsightsResource.ps1 and WindowsAppSDK issue 6774. To build anyway,
pass -AllowMissingInsightsResource.
'@
    if ($AllowMissingInsightsResource) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$msi = Join-Path $OutputDirectory "HomeAssistantDesktop-$version-x64.msi"

Write-Host 'Building the MSI...'
$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
Write-Host "  harvesting $fileCount files from $publishDir"

wix build $wxs `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -d "PublishDir=$publishDir" `
    -d "Version=$version" `
    -o $msi

if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }

$sizeMb = [math]::Round((Get-Item $msi).Length / 1MB)
Write-Host ''
Write-Host "Built $msi ($sizeMb MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'On each machine:' -ForegroundColor Cyan
Write-Host "  msiexec /i `"$(Split-Path -Leaf $msi)`" /qb STARTWITHWINDOWS=1"
Write-Host ''
Write-Host 'Then register that machine with Home Assistant so it can receive notifications:'
Write-Host '  pwsh -File Register-TickerTarget.ps1 -HaUrl http://<ha> -Token <token>'
Write-Host ''
Write-Host 'Each machine registers under its own name, so every one gets its own'
Write-Host 'device_tracker, notify service and push channel.'
