# GD-27: isolated Windows release laboratory preflight. Read-only.
# Does not call a network service, install software or submit any print job.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OfflineNuGetFeed)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-LabPrerequisite([string]$Label, [bool]$Ok) {
    if ($Ok) { Write-Host "PASS $Label" }
    else { Write-Host "FAIL $Label"; $script:failures.Add($Label) }
}
$feed = $null
try {
    $feed = (Resolve-Path -LiteralPath $OfflineNuGetFeed -ErrorAction Stop).ProviderPath
} catch {}
$validFeed = $false
if ($feed -and (Test-Path -LiteralPath $feed -PathType Container) -and
    -not ($feed.StartsWith('\\') -or $feed.StartsWith('//'))) {
    $item = Get-Item -LiteralPath $feed -Force
    $root = [IO.Path]::GetPathRoot($feed)
    if ($root) {
        $drive = ([IO.DriveInfo]::new($root)).DriveType
        $validFeed = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) -and
            ($drive -eq [IO.DriveType]::Fixed -or $drive -eq [IO.DriveType]::Removable)
    }
}
Assert-LabPrerequisite 'Local offline NuGet feed' $validFeed

$sdk = ''
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdk = ((& dotnet --version 2>$null) | Select-Object -First 1)
}
$major = 0
if ($sdk -match '^(\d+)\.') { $major = [int]$Matches[1] }
Assert-LabPrerequisite ".NET 10+ SDK ($sdk)" ($major -ge 10)
foreach ($file in @(
    'global.json', 'GetDocument.slnx', 'GetPdf.slnx',
    'tools/GetDocument.ReleaseGate/GetDocument.ReleaseGate.csproj',
    'tests/GetPdf.Tests/GetPdf.Tests.csproj',
    'tests/GetDocument.Office.Tests/GetDocument.Office.Tests.csproj',
    'src/GetDocument.PrintWindows/GetDocument.PrintWindows.csproj',
    'scripts/verify-get-document-release-windows.ps1')) {
    Assert-LabPrerequisite $file (Test-Path -LiteralPath $file -PathType Leaf)
}
$spooler = Get-Service -Name Spooler -ErrorAction SilentlyContinue
if ($spooler) {
    Write-Host "INFO Local Windows Spooler: $($spooler.Status)"
} else {
    Write-Host 'INFO Windows Spooler is not installed or not visible.'
}
foreach ($file in @('global.json', 'GetDocument.slnx')) {
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Write-Host "SHA256 $file : $((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)"
    }
}
if ($failures.Count -gt 0) {
    throw ("RELEASE LAB BLOCKED: " + ($failures -join ', '))
}
Write-Host 'PREFLIGHT PASS. Automated and physical printer tests are NOT yet performed.'
