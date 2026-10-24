# Per-user registration for GetDocument.Desktop (Windows).
# Does not modify UserChoice/default applications or legacy GetPdf registrations.
# Run only after a signed, locally validated desktop executable has been installed.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [switch]$OpenSettings
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'Windows is required for per-user file associations.'
}
$exe = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Executable does not exist: $exe"
}
if (-not [System.IO.Path]::GetExtension($exe).Equals('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'ExePath must refer to an executable (.exe).'
}

# Register only extensions the desktop routes into its own tab workspace.
# More advanced editors are deliberately NOT claimed as fully featured.
$formats = [ordered]@{
    '.pdf'  = @('GetDocument.PDF', 'PDF-документ')
    '.docx' = @('GetDocument.DOCX', 'Документ Word')
    '.xlsx' = @('GetDocument.XLSX', 'Таблица Excel')
    '.pptx' = @('GetDocument.PPTX', 'Презентация PowerPoint')
    '.png'  = @('GetDocument.Image', 'Изображение')
    '.jpg'  = @('GetDocument.Image', 'Изображение')
    '.jpeg' = @('GetDocument.Image', 'Изображение')
}
$base = 'HKCU:\Software\Classes'
$cap = 'HKCU:\Software\GET\GetDocument\Capabilities'
foreach ($extension in $formats.Keys) {
    $progId = $formats[$extension][0]
    $label = $formats[$extension][1]
    $key = "$base\$progId"
    New-Item -Path "$key\shell\open\command" -Force | Out-Null
    Set-Item -Path $key -Value "$label — ГЭТ Документ"
    Set-Item -Path "$key\shell\open\command" -Value ('"' + $exe + '" "%1"')
    New-Item -Path "$key\DefaultIcon" -Force | Out-Null
    Set-Item -Path "$key\DefaultIcon" -Value ('"' + $exe + '",0')
    New-Item -Path "$base\$extension\OpenWithProgids" -Force | Out-Null
    New-ItemProperty -Path "$base\$extension\OpenWithProgids" -Name $progId -Value '' -PropertyType String -Force | Out-Null
}
New-Item -Path "$cap\FileAssociations" -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationName -Value 'ГЭТ Документ' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationDescription -Value 'Единое окно документов ГЭТ: PDF, Office и изображения' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationIcon -Value ('"' + $exe + '",0') -PropertyType String -Force | Out-Null
foreach ($extension in $formats.Keys) {
    New-ItemProperty -Path "$cap\FileAssociations" -Name $extension -Value $formats[$extension][0] -PropertyType String -Force | Out-Null
}
New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'GetDocument' -Value 'Software\GET\GetDocument\Capabilities' -PropertyType String -Force | Out-Null

Write-Host 'ГЭТ Документ зарегистрирован в списке «Открыть с помощью». Приложения по умолчанию не изменены.'
if ($OpenSettings) { Start-Process 'ms-settings:defaultapps?registeredAppUser=GetDocument' }
