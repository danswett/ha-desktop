<#
.SYNOPSIS
    Copies Microsoft.WindowsAppRuntime.Insights.Resource.dll into a build output.

.DESCRIPTION
    Windows App SDK 2.5.1 ships a self-contained deployment that is missing this one
    file. Nothing notices until the app calls AppNotificationManager.Register(), which
    fails with 0x8007007E ("The specified module could not be found") and takes toast
    notifications down with it. The file is absent from every NuGet package in the
    dependency graph, so it cannot simply be referenced; it only exists inside the
    installed Windows App Runtime MSIX framework package.

    This is a known, still-open Windows App SDK defect:
    https://github.com/microsoft/WindowsAppSDK/issues/6774

    So we take the file from the installed framework package at build time and ship it
    beside the app. The app itself stays self-contained at runtime - it reads its own
    copy, not the framework package - so the machine it runs on still needs nothing
    installed. Only the machine that *builds* it does.

.PARAMETER TargetDirectory
    The build output to copy the file into.

.PARAMETER Version
    The Windows App SDK version to match, e.g. '2.5.1'. An exact match is strongly
    preferred; a mismatched resource DLL is worse than none.

.PARAMETER RuntimeIdentifier
    The RID being built, e.g. 'win-x64'. The framework package is installed once per
    architecture and all copies share a version, so without this filter an x86 DLL can
    win the tie and land beside an x64 app, where it will not load.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TargetDirectory,

    [string]$Version,

    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$fileName = 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
$destination = Join-Path $TargetDirectory $fileName

if (-not (Test-Path -LiteralPath $TargetDirectory)) {
    Write-Warning "Target directory does not exist: $TargetDirectory"
    exit 0
}

$packages = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue)
if ($packages.Count -eq 0) {
    Write-Warning "No Windows App Runtime framework package is installed, so $fileName could not be found."
    Write-Warning 'Toast notifications will be unavailable. Install the Windows App Runtime to fix this.'
    exit 0
}

$wanted = switch -Wildcard ($RuntimeIdentifier) {
    '*-x64'   { 'X64' }
    '*-x86'   { 'X86' }
    '*-arm64' { 'Arm64' }
    default   { 'X64' }
}

$matching = @($packages | Where-Object { $_.Architecture -eq $wanted })
if ($matching.Count -eq 0) {
    Write-Warning "No $wanted Windows App Runtime package is installed, so $fileName could not be found."
    Write-Warning 'Toast notifications will be unavailable.'
    exit 0
}

# Prefer the package whose version matches the SDK we compiled against: these resource
# DLLs are versioned alongside the runtime that loads them.
$ranked = $matching | Sort-Object -Property @{
    Expression = {
        if ($Version -and $_.Version -like "$Version*") { 0 } else { 1 }
    }
}, @{
    Expression = { [version]$_.Version }
    Descending = $true
}

$source = $null
foreach ($package in $ranked) {
    $candidate = Join-Path $package.InstallLocation $fileName
    if (Test-Path -LiteralPath $candidate) {
        $source = $candidate
        $sourceVersion = $package.Version
        break
    }
}

if (-not $source) {
    Write-Warning "$fileName was not found in any installed Windows App Runtime package."
    Write-Warning 'Toast notifications will be unavailable.'
    exit 0
}

Copy-Item -LiteralPath $source -Destination $destination -Force
if ($Version -and $sourceVersion -notlike "$Version*") {
    Write-Warning "  Copied $fileName from $wanted runtime $sourceVersion, which does not match SDK $Version."
} else {
    Write-Host "  Copied $fileName from $wanted runtime $sourceVersion."
}
