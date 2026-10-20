# IT-only, read-only offline MSI update inspection for GetPdf.
# No installation, rollback, subprocess launch, network requests or elevation.
# Signature verification relies on Windows Authenticode trust and pinned signer.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $TrustedSignerThumbprint,
    [switch] $AllowFreshInstall
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Run offline update verification locally on Windows.'
}
if ($TrustedSignerThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Specify exactly one approved SHA-1 signer thumbprint (40 hex).'
}
$trusted = $TrustedSignerThumbprint.ToUpperInvariant()
$catalog = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $catalog -PathType Container)) {
    throw 'The approved update directory is missing.'
}
$manifestFile = Join-Path $catalog 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    throw 'The release manifest is missing.'
}
if ((Get-Item -LiteralPath $manifestFile).Length -gt 32768) {
    throw 'The release manifest is unreasonably large.'
}
$manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding utf8 |
    ConvertFrom-Json -Depth 8 -ErrorAction Stop
if ($manifest.product -cne 'GET-PDF' -or $manifest.rid -cne 'win-x64' -or
    $manifest.installer -notmatch '^GetPdf-\d+\.\d+\.\d+-win-x64\.msi$') {
    throw 'Invalid product, platform or MSI filename in the release manifest.'
}
# The approval flag is procedural, not cryptographic; the independent MSI
# Authenticode and MSI Property table below are mandatory security checks.
if ($manifest.releaseApproved -ne $true -or
    $manifest.msiInstallUpgradeRemoveValidated -ne $true) {
    throw 'The package has not passed the mandatory Windows release acceptance.'
}
if ([string]$manifest.msiSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Missing approved MSI SHA-256 digest.'
}
$msi = Join-Path $catalog ([string]$manifest.installer)
$item = Get-Item -LiteralPath $msi -ErrorAction Stop
if ($item.PSIsContainer -or $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'MSI must be a regular file, not a directory or reparse point.'
}
if ($item.Length -le 0 -or $item.Length -gt 2147483648) {
    throw 'MSI size is outside the approved local update bounds.'
}
$actualHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
if ($actualHash -cne ([string]$manifest.msiSha256).ToUpperInvariant()) {
    throw 'MSI SHA-256 does not match the manifest. Do not install.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $msi
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $trusted) {
    throw 'Windows rejected Authenticode trust or the signer is not IT-approved.'
}

# Read true package identity from Windows Installer database; never use the
# untrusted manifest version as the authority for the target product.
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msi, 0)
function Read-MsiProperty($db, [string]$property) {
    if ($property -notmatch '^[A-Za-z]+$') { throw 'Invalid MSI property name.' }
    $view = $db.OpenView("SELECT Value FROM Property WHERE Property = '$property'")
    try {
        $view.Execute()
        $row = $view.Fetch()
        if ($null -eq $row) { throw "Missing MSI Property $property." }
        return [string]$row.StringData(1)
    }
    finally { $view.Close() }
}
$upgradeCode = Read-MsiProperty $db 'UpgradeCode'
$productName = Read-MsiProperty $db 'ProductName'
$productVersion = Read-MsiProperty $db 'ProductVersion'
$expectedUpgradeCode = '{BE5C1496-701B-4ED4-937E-DA3B41C2A770}'
if ($upgradeCode.ToUpperInvariant() -ne $expectedUpgradeCode -or
    $productName -cne 'ГЭТ PDF') {
    throw 'This MSI is not the approved GetPdf product or uses a different UpgradeCode.'
}
$newVersion = [version]::Parse($productVersion)
if ($newVersion.ToString(3) -cne ([version]::Parse([string]$manifest.version)).ToString(3)) {
    throw 'Package ProductVersion does not match the release manifest.'
}
$existing = @($installer.RelatedProducts($expectedUpgradeCode))
if ($existing.Count -eq 0 -and -not $AllowFreshInstall) {
    throw 'No existing GetPdf installation found. Use explicit -AllowFreshInstall to inspect a fresh install.'
}
foreach ($product in $existing) {
    $installedVersion = [version]::Parse([string]$installer.ProductInfo(
        [string]$product, 'VersionString'))
    if ($newVersion -le $installedVersion) {
        throw "Refusing downgrade or same version ($newVersion <= $installedVersion)."
    }
}

[pscustomobject]@{
    Product = 'GET-PDF'
    Msi = $item.FullName
    Sha256 = $actualHash
    SignerThumbprint = $trusted
    Version = $newVersion.ToString(3)
    InstalledVersions = @($existing | ForEach-Object {
        [string]$installer.ProductInfo([string]$_, 'VersionString')
    })
    ReleaseManifestAccepted = $true
    ReadyForManualItDecision = $true
    InstallationExecuted = $false
    RollbackExecuted = $false
}
