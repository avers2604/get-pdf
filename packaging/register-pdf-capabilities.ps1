# Register PDF handler per user; never change Windows default-app preferences.
# Mirrors the MSI registration (packaging/GetPdf.wxs) for an unpackaged build.
# Sign/approve GetPdf.exe before registration. To select a default, use Windows Settings.
param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference='Stop'
$exe=[System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }
if ([System.IO.Path]::GetExtension($exe) -ne '.exe') { throw 'Expected an EXE path' }
$classes = 'HKCU:\Software\Classes\GetPdf.PDF'
$cap = 'HKCU:\Software\GetPdf\Capabilities'
New-Item -Path $classes -Force | Out-Null
Set-Item -Path $classes -Value 'PDF-документ ГЭТ'
New-Item -Path "$classes\DefaultIcon" -Force | Out-Null
Set-Item -Path "$classes\DefaultIcon" -Value ('"'+$exe+'",0')
New-Item -Path "$classes\shell\open\command" -Force | Out-Null
Set-Item -Path "$classes\shell\open\command" -Value ('"'+$exe+'" "%1"')
New-Item -Path 'HKCU:\Software\Classes\.pdf\OpenWithProgids' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\Classes\.pdf\OpenWithProgids' -Name 'GetPdf.PDF' -Value '' -PropertyType String -Force | Out-Null
New-Item -Path "$cap\FileAssociations" -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationName -Value 'ГЭТ PDF' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationDescription -Value 'Локальный просмотр, поиск и печать PDF' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationIcon -Value ('"'+$exe+'",0') -PropertyType String -Force | Out-Null
New-ItemProperty -Path "$cap\FileAssociations" -Name '.pdf' -Value 'GetPdf.PDF' -PropertyType String -Force | Out-Null
New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'GetPdf' -Value 'Software\GetPdf\Capabilities' -PropertyType String -Force | Out-Null
Write-Host 'ГЭТ PDF registered for PDF choices. Opening Settings > Default apps to choose it.'
Start-Process 'ms-settings:defaultapps?registeredAppUser=GetPdf'
