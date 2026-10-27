# Offline-capable Windows release gate. Do not use GitHub Actions for verification.
[CmdletBinding()]
param(
    [string] $OfflineNuGetFeed = '',
    [string] $SignToolPath = '',
    [string] $SigningThumbprint = '',
    # Pre-downloaded official qpdf-<version>-msvc64.zip for offline builds; otherwise it is
    # fetched once from the qpdf GitHub release and cached in packaging\third-party.
    [string] $QpdfZip = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT') {
    throw 'Get PDF gate must run on Windows with .NET SDK 10 and WiX Toolset 5.'
}
$version = '0.1.3'
# qpdf (Apache-2.0) ships with GET PDF since 0.1.2 for lossless compression and structure checks.
# The zip is pinned to the SHA-256 published in qpdf-12.4.2.sha256 of the official release.
$qpdfVersion = '12.4.2'
$qpdfZipSha256 = 'db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d'
$qpdfUrl = "https://github.com/qpdf/qpdf/releases/download/v$qpdfVersion/qpdf-$qpdfVersion-msvc64.zip"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\GetPdf\GetPdf.csproj'
$setupProject = Join-Path $root 'src\GetPdf.Setup\GetPdf.Setup.csproj'
$tests = Join-Path $root 'tests\GetPdf.Tests\GetPdf.Tests.csproj'
$publish = Join-Path $root 'dist\win-x64'
$release = Join-Path $root 'dist\release'
$wxs = Join-Path $root 'packaging\GetPdf.wxs'
$msi = Join-Path $release "GetPdf-$version-win-x64.msi"
$setupExe = Join-Path $release "GetPdf-$version-Setup.exe"
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
Invoke-Checked 'dotnet' (@('restore', $setupProject) + $restores)
Invoke-Checked 'dotnet' @('build', $project, '-c', 'Release', '-r', 'win-x64', '--no-restore')
# MSTest.Sdk uses MTP via root global.json. Reject zero/partial discovery.
Invoke-Checked 'dotnet' @('test', $tests, '-c', 'Release', '--no-restore',
    '--minimum-expected-tests', '195')

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

# --- qpdf: verified official build into tools\qpdf with the approval manifest the app checks.
if (-not $QpdfZip) {
    $cache = Join-Path $root 'packaging\third-party'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $QpdfZip = Join-Path $cache "qpdf-$qpdfVersion-msvc64.zip"
    if (-not (Test-Path -LiteralPath $QpdfZip -PathType Leaf)) {
        Write-Host "Downloading qpdf $qpdfVersion from the official GitHub release..."
        Invoke-WebRequest -Uri $qpdfUrl -OutFile $QpdfZip -UseBasicParsing
    }
}
$actualZipSha = (Get-FileHash -LiteralPath $QpdfZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualZipSha -ne $qpdfZipSha256) {
    throw "qpdf archive SHA-256 mismatch ($actualZipSha); refusing to package it."
}
$qpdfStage = Join-Path ([System.IO.Path]::GetTempPath()) ('gpq-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Expand-Archive -LiteralPath $QpdfZip -DestinationPath $qpdfStage
    $bin = Join-Path $qpdfStage "qpdf-$qpdfVersion-msvc64\bin"
    $qpdfTarget = Join-Path $publish 'tools\qpdf'
    New-Item -ItemType Directory -Path $qpdfTarget -Force | Out-Null
    # qpdf.exe + its library + the MSVC runtime the qpdf project redistributes beside it.
    Get-ChildItem -LiteralPath $bin -File | Where-Object { $_.Name -eq 'qpdf.exe' -or $_.Extension -eq '.dll' } |
        Copy-Item -Destination $qpdfTarget
    $qpdfExe = Join-Path $qpdfTarget 'qpdf.exe'
    $reported = & $qpdfExe --version | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or $reported -notmatch [regex]::Escape($qpdfVersion)) {
        throw "Packaged qpdf does not run: $reported"
    }
    $copyright = & $qpdfExe --copyright
    Set-Content -LiteralPath (Join-Path $qpdfTarget 'LICENSE-qpdf.txt') -Encoding UTF8 -Value (
        @("qpdf $qpdfVersion - $qpdfUrl", '') + $copyright)
    # The app re-hashes qpdf.exe before every run and refuses a changed binary.
    (Get-FileHash -LiteralPath $qpdfExe -Algorithm SHA256).Hash.ToLowerInvariant() |
        Set-Content -LiteralPath (Join-Path $qpdfTarget 'qpdf.sha256') -Encoding ascii -NoNewline
}
finally {
    if (Test-Path -LiteralPath $qpdfStage) { Remove-Item -LiteralPath $qpdfStage -Recurse -Force }
}

if (Test-Path -LiteralPath $msi) { Remove-Item -LiteralPath $msi -Force }
# Russian WixUI dialogs (UI ext) and the finish-page "Default apps" launcher (Util ext).
# Once per machine: wix extension add -g WixToolset.UI.wixext/5.0.2 WixToolset.Util.wixext/5.0.2
$icon = Join-Path $root 'src\GetPdf\Assets\get-pdf.ico'
$art = Join-Path $root 'packaging\art'
Invoke-Checked 'wix' @('build', '-arch', 'x64', '-culture', 'ru-RU',
    '-ext', 'WixToolset.UI.wixext', '-ext', 'WixToolset.Util.wixext',
    '-d', "PublishDir=$publish", '-d', "IconFile=$icon",
    '-d', "BannerBmp=$art\banner.bmp", '-d', "DialogBmp=$art\dialog.bmp",
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

# --- GET DS setup wizard (users) with the MSI embedded; the bare MSI stays for IT deployment.
$setupOut = Join-Path $root 'dist\setup'
if (Test-Path -LiteralPath $setupOut) { Remove-Item -LiteralPath $setupOut -Recurse -Force }
Invoke-Checked 'dotnet' @('build', $setupProject, '-c', 'Release', '--no-restore',
    "-p:MsiPath=$msi", "-p:Version=$version", '-o', $setupOut)
Copy-Item -LiteralPath (Join-Path $setupOut 'GetPdf.Setup.exe') -Destination $setupExe -Force
if ((Get-Item -LiteralPath $setupExe).Length -lt (Get-Item -LiteralPath $msi).Length) {
    throw 'Setup.exe does not contain the MSI.'
}
if ($SignToolPath) {
    Invoke-Checked $SignToolPath @('sign', '/sha1', $SigningThumbprint, '/fd', 'SHA256', $setupExe)
    Invoke-Checked $SignToolPath @('verify', '/pa', $setupExe)
}

$exeSha = (Get-FileHash -LiteralPath (Join-Path $publish 'GetPdf.exe') -Algorithm SHA256).Hash
$msiSha = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
$setupSha = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
$manifest = [ordered]@{
    product = 'GET-PDF'
    version = $version
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
    qpdfVersion = $qpdfVersion
    qpdfZipSha256 = $qpdfZipSha256
    getPdfExeSha256 = $exeSha
    msiSha256 = $msiSha
    setupSha256 = $setupSha
    installer = [System.IO.Path]::GetFileName($setupExe)
    itPackage = [System.IO.Path]::GetFileName($msi)
}
$manifestPath = Join-Path $release 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Built MSI: $msi"
Write-Host "Built setup wizard: $setupExe"
Write-Host "Manifest: $manifestPath"
Write-Host 'Not a release approval: a test installation, PDFs, print devices and user acceptance still require verification.'
