# Windows-only manual WPF startup smoke. Not a substitute for UI tests.
# Opens actual PDF documents in the independent GetPdf.exe process and checks
# for a visible top-level window. NEVER submits a print job or closes user work.
[CmdletBinding()]
param(
    [string] $ExePath = '',
    [Parameter(Mandatory = $true)]
    [string[]] $PdfPaths,
    [int] $WindowWaitSeconds = 20,
    [string] $EvidenceFile = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'WPF smoke must run on Windows.' }
if ($PdfPaths.Count -lt 1 -or $PdfPaths.Count -gt 16) {
    throw 'Pass 1 to 16 existing PDF documents.'
}
if ($WindowWaitSeconds -lt 1 -or $WindowWaitSeconds -gt 120) {
    throw 'WindowWaitSeconds must be in range 1..120.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ExePath) {
    $ExePath = Join-Path $root 'dist\win-x64\GetPdf.exe'
}
$exe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'GetPdf.exe is missing. Run windows-build-msi.ps1 first.'
}
$fullPaths = @()
foreach ($document in $PdfPaths) {
    $full = [IO.Path]::GetFullPath($document)
    if ([IO.Path]::GetExtension($full) -ine '.pdf' -or
        -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw 'Every input must be an existing PDF file.'
    }
    $fullPaths += $full
}
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $exe
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
$start.UseShellExecute = $false
foreach ($pdf in $fullPaths) { [void]$start.ArgumentList.Add($pdf) }
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'Windows failed to start GetPdf.exe.' }

$visible = $false
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
while ($stopwatch.Elapsed.TotalSeconds -lt $WindowWaitSeconds) {
    Start-Sleep -Milliseconds 350
    $process.Refresh()
    if ($process.HasExited) {
        throw "GetPdf.exe exited early with code $($process.ExitCode)."
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        $visible = $true
        break
    }
}
if (-not $visible) {
    throw 'GetPdf.exe started but no main window appeared before timeout.'
}
$evidence = [ordered]@{
    product = 'GET-PDF'
    checkedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    documentCount = $fullPaths.Count
    processStarted = $true
    mainWindowHandleDetected = $true
    processId = $process.Id
    windowsUiAccepted = $false
    documentsActuallyRendered = $false
    testSuiteExecuted = $false
    realPrintingValidated = $false
    msInstallerValidated = $false
    releaseApproved = $false
}
if ($EvidenceFile) {
    $target = [IO.Path]::GetFullPath($EvidenceFile)
    $directory = [IO.Path]::GetDirectoryName($target)
    [void](New-Item -ItemType Directory -Force -Path $directory)
    $tmp = Join-Path $directory ('.getpdf-smoke-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $evidence | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $tmp -Encoding utf8
        Move-Item -LiteralPath $tmp -Destination $target -Force
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    }
}
[pscustomobject]$evidence
Write-Host 'GetPdf.exe has a top-level window. Keep it open and manually verify PDF pages, tabs, Cyrillic search, Ctrl+P preview and printing.'
Write-Host 'THIS SMOKE DOES NOT MARK WPF, MSI OR HARDWARE ACCEPTANCE AS PASS.'
