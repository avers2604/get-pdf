# Windows x64 offline-ready layout for GET Document + headless print worker.
# Does NOT install, set PDF defaults, or enable corporate GUP-2 access.
param(
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/get-document-win-x64')
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw 'The local .NET 10 SDK is required.'
}
$root = [IO.Path]::GetFullPath($OutputDirectory)
$desktop = Join-Path $root 'application'
$worker = Join-Path $root 'print-worker-stage'
New-Item -ItemType Directory -Force -Path $desktop, $worker | Out-Null
$desktopArguments = @('publish','src/GetDocument.Desktop/GetDocument.Desktop.csproj',
  '-c','Release','-r','win-x64','--self-contained','true',
  '-p:PublishSingleFile=false','-o',$desktop)
& dotnet @desktopArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows Avalonia publish failed.' }
$workerArguments = @('publish','src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj',
  '-c','Release','-r','win-x64','--self-contained','true',
  '-p:PublishSingleFile=false','-o',$worker)
& dotnet @workerArguments
if ($LASTEXITCODE -ne 0) { throw 'Windows print worker publish failed.' }
Copy-Item (Join-Path $worker '*') -Destination $desktop -Recurse -Force
$required = @('GetDocument.Desktop.exe', 'GetDocument.PrintWindows.exe')
foreach ($name in $required) {
  if (-not (Test-Path (Join-Path $desktop $name))) {
    throw "Missing published binary: $name"
  }
}
Write-Host "Prepared offline Windows app layout: $desktop"
Write-Host 'Run manual printer/A4/A3/duplex acceptance before deploying. No default file associations changed.'
