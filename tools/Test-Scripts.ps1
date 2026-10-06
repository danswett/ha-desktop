<#
.SYNOPSIS
    Checks the repository's PowerShell and XML for errors that do not need a Windows
    desktop to find.

.DESCRIPTION
    Most of this repo's moving parts are scripts: installation, registration, the
    installer build, and the benchmark harness. None of them is covered by a compiler,
    and a typo in one is only discovered when someone runs it - often on a machine where
    the consequence is confusing.

    So this checks what can be checked cheaply:

    - every .ps1 parses
    - PSScriptAnalyzer finds no errors, and no warnings outside an agreed list
    - every .xml / .wxs is well formed

    It is a gate against obvious breakage, not a test suite: the behaviour that actually
    matters here involves a running desktop, Home Assistant, and the Windows shell, and
    is verified by hand. See the README.

.PARAMETER Path
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER SkipAnalyzer
    Only parse; do not run PSScriptAnalyzer.

.EXAMPLE
    pwsh -File tools\Test-Scripts.ps1
#>
[CmdletBinding()]
param(
    [string]$Path,
    [switch]$SkipAnalyzer
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $Path = Split-Path -Parent $PSScriptRoot
}

$failures = [System.Collections.Generic.List[string]]::new()

# ---------------------------------------------------------------- parse ----
$scripts = @(Get-ChildItem -Path $Path -Recurse -Filter '*.ps1' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|publish|dist)\\' })

Write-Host "Parsing $($scripts.Count) PowerShell files..."
foreach ($script in $scripts) {
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$parseErrors)
    foreach ($parseError in $parseErrors) {
        # The -f expression needs its own parentheses: inside a method call, the comma
        # would otherwise bind to Add's argument list instead of the format operator.
        $failures.Add(('{0}({1}): {2}' -f $script.Name, $parseError.Extent.StartLineNumber, $parseError.Message))
    }
}

# ------------------------------------------------------------------ xml ----
$documents = @(Get-ChildItem -Path $Path -Recurse -Include '*.xml', '*.wxs', '*.config' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|publish|dist)\\' })

Write-Host "Checking $($documents.Count) XML files..."
foreach ($document in $documents) {
    try {
        [void][xml](Get-Content $document.FullName -Raw)
    }
    catch {
        $failures.Add(('{0}: {1}' -f $document.Name, $_.Exception.Message))
    }
}

# ------------------------------------------------------------- analyzer ----
if (-not $SkipAnalyzer) {
    if (Get-Module -ListAvailable -Name PSScriptAnalyzer) {
        Import-Module PSScriptAnalyzer

        # Write-Host is the right call for a script whose entire output is for a person
        # watching it run, and these scripts deliberately change machine state.
        $exclude = @(
            'PSAvoidUsingWriteHost',
            'PSUseShouldProcessForStateChangingFunctions',
            'PSAvoidUsingConvertToSecureStringWithPlainText',
            'PSUseSingularNouns'
        )

        Write-Host 'Running PSScriptAnalyzer...'

        # bench/ is measurement scaffolding, kept so the numbers in the README can be
        # reproduced rather than because anyone ships it. Its empty catch blocks are
        # deliberate - best-effort cleanup of test windows must not derail a run - so it
        # is held to "must not be broken" rather than to the style bar the rest meets.
        $shipped = @('src', 'tools', 'build') |
            ForEach-Object { Join-Path $Path $_ } |
            Where-Object { Test-Path $_ }

        $results = @(
            foreach ($directory in $shipped) {
                Invoke-ScriptAnalyzer -Path $directory -Recurse -Severity Error, Warning -ExcludeRule $exclude
            }

            $benchPath = Join-Path $Path 'bench'
            if (Test-Path $benchPath) {
                Invoke-ScriptAnalyzer -Path $benchPath -Recurse -Severity Error -ExcludeRule $exclude
            }
        ) | Where-Object { $_.ScriptPath -notmatch '\\(bin|obj|publish|dist)\\' }

        foreach ($result in $results) {
            $failures.Add(('{0}({1}): {2} {3}' -f (Split-Path -Leaf $result.ScriptPath), $result.Line, $result.RuleName, $result.Message))
        }
    }
    else {
        Write-Warning 'PSScriptAnalyzer is not installed; skipping. Install-Module PSScriptAnalyzer -Scope CurrentUser'
    }
}

# ---------------------------------------------------------------- result ----
Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) problem(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'All checks passed.' -ForegroundColor Green
