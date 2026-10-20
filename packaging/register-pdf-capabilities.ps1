# Register PDF handler per user; never change Windows default-app preferences.
# Sign/approve GetPdf.exe before registration. To select a default, use Windows Settings.
param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference='Stop'
$exe=[System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }
if ([System.IO.Path]::GetExtension($exe) -ne '.exe') { throw 'Expected an EXE path' }
$classes = 'HKCU:\Software\Classes\GetPdf.PDF'
$cap = 'HKCU:\Software\GetPdf\Capabilities'
New-Item -Path $classes -Force | Out-Null
Set-Item -Path $classes -Value 'ГЭТ PDF PDF document'
New-Item -Path "$classes\DefaultIcon" -Force | Out-Null
Set-Item -Path "$classes\DefaultIcon" -Value ('"'+$exe+'",0')
New-Item -Path "$classes\shell\open\command" -Force | Out-Null
Set-Item -Path "$classes\shell\open\command" -Value ('"'+$exe+'" "%1"')
New-Item -Path "$cap\FileAssociations" -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationName -Value 'ГЭТ PDF' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $cap -Name ApplicationDescription -Value 'Локальный просмотр, печать и редактирование PDF' -PropertyType String -Force | Out-Null
New-ItemProperty -Path "$cap\FileAssociations" -Name '.pdf' -Value 'GetPdf.PDF' -PropertyType String -Force | Out-Null
New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'ГЭТ PDF' -Value 'Software\GetPdf\Capabilities' -PropertyType String -Force | Out-Null
Write-Host 'ГЭТ PDF registered for PDF choices. Select default under Windows Settings > Apps > Default apps.'
