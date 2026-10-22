# Offline-capable Windows release gate. Do not use GitHub Actions for verification.
[CmdletBinding()]
param(
    [string] $OfflineNuGetFeed = '',
    [string] $SignToolPath = '',
    [string] $SigningThumbprint = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT') {
    throw 'Get PDF gate must run on Windows with .NET SDK 10 and WiX Toolset 5.'
}
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\GetPdf\GetPdf.csproj'
$tests = Join-Path $root 'tests\GetPdf.Tests\GetPdf.Tests.csproj'
$publish = Join-Path $root 'dist\win-x64'
$release = Join-Path $root 'dist\release'
$wxs = Join-Path $root 'packaging\GetPdf.wxs'
$msi = Join-Path $release 'GetPdf-0.1.0-win-x64.msi'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found.' }
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw 'WiX Toolset 5 CLI not found.' }
if ($OfflineNuGetFeed -and -not (Test-Path -LiteralPath $OfflineNuGetFeed -PathType Container)) {
    throw 'Offline NuGet feed does not exist.'
}
if ([bool]$SignToolPath -ne [bool]$SigningThumbprint) {
    throw 'Signing requires BOTH a SignToolPath and SigningThumbprint.'
}
if ($SigningThumbprint -and $SigningThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'SigningThumbprint must be a SHA-1 certificate thumbprint (40 hex characters).'
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE"
    }
}
$restores = @()
if ($OfflineNuGetFeed) { $restores = @('--source', $OfflineNuGetFeed) }
Invoke-Checked 'dotnet' (@('restore', $project, '-r', 'win-x64') + $restores)
Invoke-Checked 'dotnet' (@('restore', $tests) + $restores)
Invoke-Checked 'dotnet' @('build', $project, '-c', 'Release', '-r', 'win-x64', '--no-restore')
# MSTest.Sdk uses MTP via root global.json. Reject zero/partial discovery.
Invoke-Checked 'dotnet' @('test', $tests, '-c', 'Release', '--no-restore',
    '--minimum-expected-tests', '180')

# A dirty publish dir could carry old TWAIN/scan binaries into a new MSI.
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null
Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:PublishSingleFile=false', '--no-restore', '-o', $publish)
if (-not (Test-Path -LiteralPath (Join-Path $publish 'GetPdf.exe') -PathType Leaf)) {
    throw 'GetPdf.exe is absent from fresh publish output.'
}
$forbidden = Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object {
    $_.Name -match '^(GupScan|TwainX86|DriverCatalog|Naps2Worker)' -or
    $_.Name -match 'NAPS2\.Sdk\.Worker\.Win32'
}
if ($forbidden) {
    throw 'A scanner/driver worker is included in the PDF publish payload.'
}
if (Test-Path -LiteralPath $msi) { Remove-Item -LiteralPath $msi -Force }
Invoke-Checked 'wix' @('build', '-arch', 'x64', '-d', "PublishDir=$publish",
    '-o', $msi, $wxs)
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) {
    throw 'WiX did not produce an MSI.'
}
if ($SignToolPath) {
    if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw 'Specified signtool is missing.'
    }
    # Offline Authenticode: no external timestamp server is contacted.
    Invoke-Checked $SignToolPath @('sign', '/sha1', $SigningThumbprint, '/fd', 'SHA256', $msi)
    Invoke-Checked $SignToolPath @('verify', '/pa', $msi)
}
$exeSha = (Get-FileHash -LiteralPath (Join-Path $publish 'GetPdf.exe') -Algorithm SHA256).Hash
$msiSha = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
$manifest = [ordered]@{
    product = 'GET-PDF'
    version = '0.1.0'
    rid = 'win-x64'
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    testedLocally = $true
    windowsHardwareValidated = $false
    wpfUiValidated = $false
    acroFormCyrillicValidated = $false
    signatureTrustValidated = $false
    vectorPrintValidated = $false
    printerDriverValidated = $false
    msiInstallUpgradeRemoveValidated = $false
    releaseApproved = $false
    signed = [bool]$SignToolPath
    getPdfExeSha256 = $exeSha
    msiSha256 = $msiSha
    installer = [System.IO.Path]::GetFileName($msi)
}
$manifestPath = Join-Path $release 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Built MSI: $msi"
Write-Host "Manifest: $manifestPath"
Write-Host 'Not a release approval: a test installation, PDFs, print devices and user acceptance still require verification.'
