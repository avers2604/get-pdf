# GD-23: local Windows verification using ONLY a supplied offline NuGet feed.
# Example: pwsh -File scripts/verify-get-document-offline-windows.ps1 -OfflineNuGetFeed D:\get\nuget-feed
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OfflineNuGetFeed)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if ($OfflineNuGetFeed -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
    throw 'Network NuGet sources are prohibited for this acceptance run.'
}
$resolved = Resolve-Path -LiteralPath $OfflineNuGetFeed -ErrorAction Stop
$feed = $resolved.ProviderPath
if (-not (Test-Path -LiteralPath $feed -PathType Container)) {
    throw 'OfflineNuGetFeed must be an existing local directory.'
}
# A UNC share, mapped network drive or junction can contact external servers.
# The release-acceptance feed must reside on a physical local volume.
if ($feed.StartsWith('\\') -or $feed.StartsWith('//')) {
    throw 'OfflineNuGetFeed cannot be a UNC network path.'
}
$feedItem = Get-Item -LiteralPath $feed -Force
if (($feedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'OfflineNuGetFeed cannot be a symlink or junction.'
}
$root = [IO.Path]::GetPathRoot($feed)
if ([string]::IsNullOrWhiteSpace($root)) {
    throw 'OfflineNuGetFeed must use a local drive.'
}
$driveType = ([IO.DriveInfo]::new($root)).DriveType
if ($driveType -ne [IO.DriveType]::Fixed -and
    $driveType -ne [IO.DriveType]::Removable) {
    throw 'OfflineNuGetFeed must be on a local fixed or removable drive.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK not installed.'
}
$major = [int]((dotnet --version).Split('.')[0])
if ($major -lt 10) { throw '.NET 10 SDK required.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
# The --source argument overrides user/machine feeds for this restore.
# NuGetAudit is deliberately disabled in the sealed network. Pin and audit the
# offline NuGet bundle before deployment via approved organizational controls.
& dotnet restore GetDocument.slnx --source $feed '-p:NuGetAudit=false'
if ($LASTEXITCODE -ne 0) { throw 'Offline NuGet restore failed.' }
$commands = @(
  @('build','GetDocument.slnx','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Printing.Tests/GetDocument.Printing.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Images.Tests/GetDocument.Images.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Revisions.Tests/GetDocument.Revisions.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Collaboration.Tests/GetDocument.Collaboration.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.WopiGateway.Tests/GetDocument.WopiGateway.Tests.csproj','-c','Release','--no-restore')
)
foreach ($argsList in $commands) {
    & dotnet @argsList
    if ($LASTEXITCODE -ne 0) {
        throw ('GET Document offline verification failed: dotnet ' + ($argsList -join ' '))
    }
}
& dotnet restore src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj --source $feed '-p:NuGetAudit=false'
if ($LASTEXITCODE -ne 0) { throw 'Offline Windows PrintWorker restore failed.' }
& dotnet build src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Offline Windows PrintWorker build failed.' }
Write-Host 'PASS: offline .NET build and tests. Physical printer trials still required.'
