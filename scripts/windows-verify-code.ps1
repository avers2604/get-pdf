# Run the Windows WPF build and MSTest checks without requiring WiX/MSI.
# This is a local verification command, not GitHub Actions or release approval.
[CmdletBinding()]
param(
    [string] $OfflineNuGetFeed = '',
    [string] $Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The WPF verification gate requires Windows.' }
if ($Configuration -notin @('Debug', 'Release')) { throw 'Unsupported configuration.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK is missing.' }

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $root 'GetPdf.slnx'
$testProject = Join-Path $root 'tests\GetPdf.Tests\GetPdf.Tests.csproj'
$runnerFile = Join-Path $root 'global.json'
if (-not (Test-Path -LiteralPath $runnerFile -PathType Leaf)) {
    throw 'MSTest runner configuration is missing.'
}
$runnerConfig = Get-Content -LiteralPath $runnerFile -Raw | ConvertFrom-Json
if ($runnerConfig.test.runner -ne 'Microsoft.Testing.Platform') {
    throw 'Use .NET 10 Microsoft.Testing.Platform runner, not VSTest fallback.'
}
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw "Expected .NET 10 SDK; found $sdkVersion"
}
$restoreArguments = @('restore', $solution)
if ($OfflineNuGetFeed) {
    if (-not (Test-Path -LiteralPath $OfflineNuGetFeed -PathType Container)) {
        throw 'Approved local NuGet feed is missing.'
    }
    $restoreArguments += @('--source', $OfflineNuGetFeed)
}
function Invoke-Checked([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("dotnet " + ($Arguments -join ' ') + " exited with code $LASTEXITCODE")
    }
}
# No reports are claimed until every stage succeeds; no test bypasses.
Push-Location $root
try {
    Invoke-Checked $restoreArguments
    Invoke-Checked @('build', $solution, '-c', $Configuration, '--no-restore')
    Invoke-Checked @('test', $testProject, '-c', $Configuration, '--no-restore',
        '--minimum-expected-tests', '192')
}
finally { Pop-Location }
Write-Host 'Local WPF build and MSTest execution succeeded.'
Write-Host 'Still NOT validated: signed MSI, GOST/CRL, physical printers, WPF UI, or GUP-2 WORM.'
