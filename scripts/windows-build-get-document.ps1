# Offline-capable Windows release gate for ГЭТ Документ. Do not use GitHub Actions for verification.
# Produces dist\get-document\release\GetDocument-<v>-Setup.exe (GET DS wizard, MSI inside),
# GetDocument-<v>-win-x64.msi for IT and release-manifest.json.
[CmdletBinding()]
param(
    [string] $OfflineNuGetFeed = '',
    # Pre-downloaded pinned archives for offline builds; otherwise fetched once into packaging\third-party.
    [string] $QpdfZip = '',
    [string] $LibreOfficeMsi = '',
    # The MSI (~0.3 GB with LibreOffice) normally travels inside Setup.exe; -SideBySideMsi ships
    # Setup.exe and the MSI as two files that must stay in one folder.
    [switch] $SideBySideMsi,
    [switch] $SkipVerify
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT') {
    throw 'The GET Document gate must run on Windows with .NET SDK 10 and WiX Toolset 5.'
}
$version = '0.1.0'
$qpdfVersion = '12.4.2'
$qpdfZipSha256 = 'db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d'
$qpdfUrl = "https://github.com/qpdf/qpdf/releases/download/v$qpdfVersion/qpdf-$qpdfVersion-msvc64.zip"
# LibreOffice (MPL-2.0) is installed privately into libreoffice\ for faithful Office preview,
# editing and printing; it does not register itself with Windows or touch a system LibreOffice.
$libreOfficeVersion = '26.2.6'
$libreOfficeMsiSha256 = 'f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660'
$libreOfficeUrl = "https://download.documentfoundation.org/libreoffice/stable/$libreOfficeVersion/win/x86_64/LibreOffice_${libreOfficeVersion}_Win_x86-64.msi"
# UI languages kept from LibreOffice (everything else is ~0.8 GB of translations and dictionaries).
$keepLanguages = @('ru', 'en-US', 'en_US', 'en-GB', 'en_GB', 'en')

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$desktopProject = Join-Path $root 'src\GetDocument.Desktop\GetDocument.Desktop.csproj'
$workerProject = Join-Path $root 'src\GetDocument.PrintWindows\GetDocument.PrintWindows.csproj'
$setupProject = Join-Path $root 'src\GetPdf.Setup\GetPdf.Setup.csproj'
$out = Join-Path $root 'dist\get-document'
$publish = Join-Path $out 'win-x64'
$release = Join-Path $out 'release'
$cache = Join-Path $root 'packaging\third-party'
$wxs = Join-Path $root 'packaging\GetDocument.wxs'
$msi = Join-Path $release "GetDocument-$version-win-x64.msi"
$setupExe = Join-Path $release "GetDocument-$version-Setup.exe"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found.' }
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw 'WiX Toolset 5 CLI not found.' }
if ($OfflineNuGetFeed -and -not (Test-Path -LiteralPath $OfflineNuGetFeed -PathType Container)) {
    throw 'Offline NuGet feed does not exist.'
}
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}
function Get-Pinned([string]$Given, [string]$Name, [string]$Url, [string]$Sha256) {
    $path = $Given
    if (-not $path) {
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $path = Join-Path $cache $Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Host "Downloading $Name from the official release..."
            Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
        }
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "$Name SHA-256 mismatch ($actual); refusing to package it." }
    return $path
}

if (-not $SkipVerify) {
    & (Join-Path $root 'scripts\verify-get-document-windows.ps1')
    if (-not $?) { throw 'verify-get-document-windows.ps1 failed.' }
}

# --- Application + headless print worker, self-contained (no .NET runtime needed on the PC).
foreach ($dir in @($publish, $release)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
$restores = @()
if ($OfflineNuGetFeed) { $restores = @('--source', $OfflineNuGetFeed) }
foreach ($project in @($desktopProject, $workerProject)) {
    Invoke-Checked 'dotnet' (@('restore', $project, '-r', 'win-x64') + $restores)
    Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', "-p:Version=$version", '--no-restore', '-o', $publish)
}
foreach ($name in @('GetDocument.Desktop.exe', 'GetDocument.PrintWindows.exe', 'Assets\pdf-document.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $name) -PathType Leaf)) {
        throw "$name is absent from the fresh publish output."
    }
}

# --- qpdf: same pinned official build and approval manifest as GET PDF (tools\qpdf).
$QpdfZip = Get-Pinned $QpdfZip "qpdf-$qpdfVersion-msvc64.zip" $qpdfUrl $qpdfZipSha256
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('gdq-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Expand-Archive -LiteralPath $QpdfZip -DestinationPath $stage
    $bin = Join-Path $stage "qpdf-$qpdfVersion-msvc64\bin"
    $qpdfTarget = Join-Path $publish 'tools\qpdf'
    New-Item -ItemType Directory -Path $qpdfTarget -Force | Out-Null
    Get-ChildItem -LiteralPath $bin -File | Where-Object { $_.Name -eq 'qpdf.exe' -or $_.Extension -eq '.dll' } |
        Copy-Item -Destination $qpdfTarget
    $qpdfExe = Join-Path $qpdfTarget 'qpdf.exe'
    $reported = & $qpdfExe --version | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or $reported -notmatch [regex]::Escape($qpdfVersion)) { throw "Packaged qpdf does not run: $reported" }
    Set-Content -LiteralPath (Join-Path $qpdfTarget 'LICENSE-qpdf.txt') -Encoding UTF8 -Value (
        @("qpdf $qpdfVersion - $qpdfUrl", '') + (& $qpdfExe --copyright))
    (Get-FileHash -LiteralPath $qpdfExe -Algorithm SHA256).Hash.ToLowerInvariant() |
        Set-Content -LiteralPath (Join-Path $qpdfTarget 'qpdf.sha256') -Encoding ascii -NoNewline
}
finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }

# --- LibreOffice: administrative image of the pinned MSI (no install, no admin rights), trimmed once
# into packaging\third-party\libreoffice-<v> and harvested from there by WiX (no second ~0.7 GB copy).
$LibreOfficeMsi = Get-Pinned $LibreOfficeMsi "LibreOffice_${libreOfficeVersion}_Win_x86-64.msi" $libreOfficeUrl $libreOfficeMsiSha256
$lo = Join-Path $cache "libreoffice-$libreOfficeVersion"
$trimmedMarker = Join-Path $lo '.getdoc-trimmed'
if (-not (Test-Path -LiteralPath $trimmedMarker)) {
    $image = Join-Path $cache "libreoffice-$libreOfficeVersion-image"
    if (-not (Test-Path -LiteralPath (Join-Path $image 'program\soffice.exe'))) {
        if (Test-Path -LiteralPath $image) { Remove-Item -LiteralPath $image -Recurse -Force }
        $log = Join-Path ([System.IO.Path]::GetTempPath()) "gd-libreoffice-admin-$libreOfficeVersion.log"
        $process = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/a', "`"$LibreOfficeMsi`"", '/qn', "TARGETDIR=`"$image`"", '/l*v', "`"$log`"")
        if ($process.ExitCode -ne 0) {
            throw "LibreOffice administrative image failed ($($process.ExitCode)); see $log (needs ~1.6 GB free)."
        }
    }
    if (Test-Path -LiteralPath $lo) { Remove-Item -LiteralPath $lo -Recurse -Force }
    Move-Item -LiteralPath $image -Destination $lo
    Write-Host 'Trimming LibreOffice to Russian and English...'
    # The admin image keeps the VC++ runtime and fonts where the MSI would have put them system-wide;
    # a private copy needs them beside soffice.exe and in share\fonts (picked up without installing).
    Get-ChildItem -LiteralPath (Join-Path $lo 'System64') -Filter *.dll | Copy-Item -Destination (Join-Path $lo 'program') -Force
    $fonts = Join-Path $lo 'share\fonts\truetype'
    New-Item -ItemType Directory -Path $fonts -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $lo 'Fonts') -File | Move-Item -Destination $fonts -Force
    foreach ($dir in @('System64', 'System', 'Fonts')) {
        $path = Join-Path $lo $dir
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Get-ChildItem -LiteralPath $lo -File -Filter *.msi | Remove-Item -Force

    function Test-KeptLanguage([string]$Name) {
        foreach ($language in $keepLanguages) { if ($Name -ieq $language) { return $true } }
        return $false
    }
    # UI translations, help and per-language templates: language-named subfolders.
    foreach ($parent in @('program\resource', 'share\autotext', 'share\template', 'share\wordbook', 'readmes', 'help')) {
        $path = Join-Path $lo $parent
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Get-ChildItem -LiteralPath $path -Directory | Where-Object {
            $_.Name -notin @('common', 'media', 'wizard') -and -not (Test-KeptLanguage $_.Name)
        } | Remove-Item -Recurse -Force
    }
    # Language packs in the configuration registry: Langpack-xx.xcd, res\registry_xx.xcd, res\fcfg_langpack_xx.xcd.
    Get-ChildItem -LiteralPath (Join-Path $lo 'share\registry') -File -Recurse | Where-Object {
        $_.Name -match '^(Langpack-|registry_|fcfg_langpack_)(.+)\.xcd$' -and -not (Test-KeptLanguage $Matches[2])
    } | Remove-Item -Force
    # No online update checks from a private, centrally updated copy.
    Remove-Item -LiteralPath (Join-Path $lo 'share\registry\onlineupdate.xcd') -Force -ErrorAction SilentlyContinue
    # Spelling dictionaries: Russian and English only; Java-based extensions are not used headless.
    Get-ChildItem -LiteralPath (Join-Path $lo 'share\extensions') -Directory | Where-Object {
        $_.Name -notin @('dict-ru', 'dict-en')
    } | Remove-Item -Recurse -Force
    # One icon theme (Colibre, the Windows default) instead of eleven.
    Get-ChildItem -LiteralPath (Join-Path $lo 'share\config') -Filter 'images_*.zip' | Where-Object {
        $_.Name -notin @('images_colibre.zip', 'images_colibre_svg.zip', 'images_helpimg.zip')
    } | Remove-Item -Force
    Set-Content -LiteralPath $trimmedMarker -Value $libreOfficeMsiSha256 -Encoding ascii
}

$soffice = Join-Path $lo 'program\soffice.com'
$reported = & $soffice --headless --version 2>&1 | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or "$reported" -notmatch [regex]::Escape($libreOfficeVersion)) {
    throw "Packaged LibreOffice does not run: $reported"
}
$loSize = (Get-ChildItem -LiteralPath $lo -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("LibreOffice payload: {0:N0} MB" -f ($loSize / 1MB))

# --- MSI
$icon = Join-Path $root 'src\GetDocument.Desktop\Assets\get-document.ico'
$art = Join-Path $root 'packaging\art\get-document'
# Once per machine: wix extension add -g WixToolset.UI.wixext/5.0.2 WixToolset.Util.wixext/5.0.2
Invoke-Checked 'wix' @('build', '-arch', 'x64', '-culture', 'ru-RU',
    '-ext', 'WixToolset.UI.wixext', '-ext', 'WixToolset.Util.wixext',
    '-d', "Version=$version.0", '-d', "PublishDir=$publish", '-d', "LibreOfficeDir=$lo", '-d', "IconFile=$icon",
    '-d', "BannerBmp=$art\banner.bmp", '-d', "DialogBmp=$art\dialog.bmp",
    '-o', $msi, $wxs)
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw 'WiX did not produce an MSI.' }

# --- GET DS setup wizard (the same wizard as GET PDF, built for GET Document)
$setupOut = Join-Path $out 'setup'
if (Test-Path -LiteralPath $setupOut) { Remove-Item -LiteralPath $setupOut -Recurse -Force }
Invoke-Checked 'dotnet' (@('restore', $setupProject) + $restores)
$setupArguments = @('build', $setupProject, '-c', 'Release', '--no-restore', '-p:SetupProduct=GetDocument',
    "-p:Version=$version", '-o', $setupOut)
if (-not $SideBySideMsi) { $setupArguments += "-p:MsiPath=$msi" }
Invoke-Checked 'dotnet' $setupArguments
Copy-Item -LiteralPath (Join-Path $setupOut 'GetDocument.Setup.exe') -Destination $setupExe -Force
if (-not $SideBySideMsi -and (Get-Item -LiteralPath $setupExe).Length -lt (Get-Item -LiteralPath $msi).Length) {
    throw 'Setup.exe does not contain the MSI.'
}

$manifest = [ordered]@{
    product = 'GET-Document'
    version = $version
    rid = 'win-x64'
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    testedLocally = -not $SkipVerify
    msiInstallUpgradeRemoveValidated = $false
    releaseApproved = $false
    signed = $false
    qpdfVersion = $qpdfVersion
    libreOfficeVersion = $libreOfficeVersion
    libreOfficeMsiSha256 = $libreOfficeMsiSha256
    desktopExeSha256 = (Get-FileHash -LiteralPath (Join-Path $publish 'GetDocument.Desktop.exe') -Algorithm SHA256).Hash
    msiSha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    setupSha256 = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
    installer = [System.IO.Path]::GetFileName($setupExe)
    msiEmbedded = -not $SideBySideMsi
    itPackage = [System.IO.Path]::GetFileName($msi)
}
$manifestPath = Join-Path $release 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Built MSI: $msi"
Write-Host "Built setup wizard: $setupExe"
Write-Host "Manifest: $manifestPath"
Write-Host 'Not a release approval: a test installation, Office documents, print devices and user acceptance still require verification.'
