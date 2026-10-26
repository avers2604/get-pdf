# GD-25 release gate. Only runs on an approved offline Windows workstation.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OfflineNuGetFeed)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
& (Join-Path $PSScriptRoot 'verify-get-document-offline-windows.ps1') -OfflineNuGetFeed $OfflineNuGetFeed
if ($LASTEXITCODE -ne 0) { throw 'Offline Windows validation failed.' }

# Repeat the feed validation for the only new project; do not restore from
# the user's default NuGet configuration or a mapped network drive.
$feed = (Resolve-Path -LiteralPath $OfflineNuGetFeed -ErrorAction Stop).ProviderPath
if ($feed.StartsWith('\\') -or $feed.StartsWith('//')) {
    throw 'NuGet release feed must be local, not UNC.'
}
$item = Get-Item -LiteralPath $feed -Force
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'NuGet release feed cannot be a symlink/junction.'
}
$root = [IO.Path]::GetPathRoot($feed)
$driveType = ([IO.DriveInfo]::new($root)).DriveType
if ($driveType -ne [IO.DriveType]::Fixed -and
    $driveType -ne [IO.DriveType]::Removable) {
    throw 'NuGet release feed must be on a local fixed/removable drive.'
}
& dotnet restore tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj --source $feed '-p:NuGetAudit=false'
if ($LASTEXITCODE -ne 0) { throw 'ReleaseGate restore failed.' }
& dotnet build tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'ReleaseGate build failed.' }
& dotnet run --project tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj -c Release --no-build --no-restore -- --self-test
if ($LASTEXITCODE -ne 0) { throw 'ReleaseGate self-tests failed.' }

# Reports may contain document paths: use the private current-user data area,
# not a shared source checkout or publicly readable artifacts directory.
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'Current-user LocalApplicationData is not available.'
}
$reports = Join-Path $env:LOCALAPPDATA ('GET-Document\ReleaseReports\gd25-windows-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $reports | Out-Null
Write-Host "Local TRX reports: $reports"
foreach ($suite in @('Core','Office','Printing','Images','Revisions','Collaboration','WopiGateway')) {
    $project = "tests/GetDocument.$suite.Tests/GetDocument.$suite.Tests.csproj"
    # Native MTP reporting. A skipped critical test does NOT satisfy the gate.
    & dotnet test --project $project -c Release --no-restore -- --report-trx --report-trx-filename "$suite.trx" --results-directory $reports
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed: $suite" }
}
& dotnet test --project tests/GetPdf.Tests/GetPdf.Tests.csproj -c Release --no-restore -- --report-trx --report-trx-filename 'LegacyPdf.trx' --results-directory $reports
if ($LASTEXITCODE -ne 0) { throw 'Legacy GetPdf regression tests failed.' }
& dotnet run --project tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj -c Release --no-build --no-restore -- $reports windows
if ($LASTEXITCODE -ne 0) { throw 'Critical GD-25 release gate test missing/failed/inconclusive.' }
Write-Host 'PASS: GD-25 Windows automated gate; physical printer acceptance remains mandatory.'
