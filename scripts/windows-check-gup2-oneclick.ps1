# Read-only workstation preflight for the IT-approved one-click PDF handoff.
# Never writes Edge policy, Windows UserChoice, credentials or documents.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApprovedOrigin
)

$ErrorActionPreference = 'Stop'
$origin = $null
if (-not [Uri]::TryCreate($ApprovedOrigin, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($origin.Host) -or
    $origin.UserInfo -or $origin.Query -or $origin.Fragment -or
    $origin.AbsolutePath -ne '/') {
    throw 'ApprovedOrigin must be a bare corporate HTTPS origin, without credentials, path, query or fragment.'
}
$normalized = $origin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
$policyRoot = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'

function Get-PolicyList([string]$name) {
    $key = Join-Path $policyRoot $name
    if (-not (Test-Path -LiteralPath $key)) { return @() }
    $properties = Get-ItemProperty -LiteralPath $key
    return @($properties.PSObject.Properties |
        Where-Object { $_.Name -match '^[1-9][0-9]*$' } |
        Sort-Object { [int]$_.Name } |
        ForEach-Object { [string]$_.Value })
}

$types = @(Get-PolicyList 'AutoOpenFileTypes')
$origins = @(Get-PolicyList 'AutoOpenAllowedForURLs')
$typesAllowed = $types -contains 'pdf'
# Be strict: any other origin enables the PDF auto-open rule there as well.
$originAllowed = $origins.Count -eq 1 -and $origins[0].TrimEnd('/') -ceq $normalized
$choicePath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice'
$selectedHandler = ''
if (Test-Path -LiteralPath $choicePath) {
    $selectedHandler = [string](Get-ItemProperty -LiteralPath $choicePath).ProgId
}
$handlerAllowed = $selectedHandler -in @('GetPdf.PDF', 'Applications\GetPdf.exe')

[pscustomobject]@{
    ApprovedOrigin = $normalized
    AutoOpenPdfEnabled = $typesAllowed
    PdfRestrictedToApprovedOrigin = $originAllowed
    WindowsHandler = $selectedHandler
    GetPdfSelected = $handlerAllowed
    ReadyForManualBrowserTest = ($typesAllowed -and $originAllowed -and $handlerAllowed)
    ChangesMade = $false
} | ConvertTo-Json -Depth 2

# A policy snapshot is not evidence of actual browser behavior; exercise
# Edge / GUP-2 download revocation, SmartScreen and Windows association.
