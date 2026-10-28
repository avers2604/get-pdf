# GD-31I — developer/IT-only offline acceptance. No GitHub Actions.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string] $OfflineNuGetFeed,
    [string] $Report = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Use Linux GD-31I entry point on Linux.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    throw 'Python 3.10+ is required on the QA machine to generate a JSON acceptance report. It is NOT a GET Documents runtime dependency.'
}
$argsList = @((Join-Path $PSScriptRoot 'office_acceptance.py'),
              '--feed', $OfflineNuGetFeed)
if ($Report) { $argsList += @('--report', $Report) }
Push-Location $root
try {
    & $python.Source @argsList
    $result = $LASTEXITCODE
}
finally { Pop-Location }
if ($result -ne 0) { throw "GD-31I local acceptance failed or was blocked (exit $result); inspect the generated JSON." }
Write-Host 'Only automated checks passed. Release approval remains FALSE until manual gates are signed off.'
