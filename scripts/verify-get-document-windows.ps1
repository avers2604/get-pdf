# Local Windows validation. GitHub Actions remain disabled.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw '.NET 10 SDK not found on this workstation.'
}
$major = [int]((dotnet --version).Split('.')[0])
if ($major -lt 10) { throw '.NET 10 SDK is required.' }
$commands = @(
  @('restore','GetDocument.slnx'),
  @('build','GetDocument.slnx','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Core.Tests/GetDocument.Core.Tests.csproj','-c','Release','--no-restore'),
  @('test','tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj','-c','Release','--no-restore'),
  @('build','GetPdf.slnx','-c','Release'),
  @('test','tests/GetPdf.Tests/GetPdf.Tests.csproj','-c','Release','--no-restore')
)
foreach ($argsList in $commands) {
  & dotnet @argsList
  if ($LASTEXITCODE -ne 0) { throw ("Local verification failed: dotnet " + ($argsList -join ' ')) }
}
Write-Host 'PASS: local .NET build and tests completed. Manual Windows UI/SMB checks are still required.'
