# Regenerates the GET DS 4.1 artwork for the classic WixUI dialogs (packaging/art/*.bmp).
# WixUI sizes: banner 493x58 (dialog title is drawn on its left), dialog 493x312 (Welcome/Finish:
# the left 164 px are artwork, text is drawn on the right).
[CmdletBinding()]
param(
    [string] $Product = 'ГЭТ PDF',
    [string] $Tagline = 'Просмотр и печать PDF',
    # Default packaging\art (GET PDF); GET Document uses packaging\art\get-document.
    [string] $OutDir = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$art = if ($OutDir) { [System.IO.Path]::GetFullPath($OutDir) } else { Join-Path $root 'packaging\art' }
New-Item -ItemType Directory -Path $art -Force | Out-Null
$navy = [System.Drawing.Color]::FromArgb(0x24, 0x30, 0x5E)
$teal = [System.Drawing.Color]::FromArgb(0x00, 0x9C, 0xBC)
$line = [System.Drawing.Color]::FromArgb(0xE3, 0xE6, 0xEE)
$markColor = [System.Drawing.Image]::FromFile((Join-Path $root 'src\GetPdf\Assets\get-mark.png'))
$markInverse = [System.Drawing.Image]::FromFile((Join-Path $root 'src\GetPdf\Assets\get-mark-inverse.png'))

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $g.Clear([System.Drawing.Color]::White)
    return @($bmp, $g)
}

# Banner: white, thin DS divider at the bottom, colour mark on the right.
$bmp, $g = New-Canvas 493 58
$g.FillRectangle((New-Object System.Drawing.SolidBrush($line)), 0, 57, 493, 1)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($teal)), 0, 55, 96, 2)
$g.DrawImage($markColor, 441, 9, 40, 40)
$bmp.Save((Join-Path $art 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

# Dialog: navy rail with the inverse mark and product name, white text area.
$bmp, $g = New-Canvas 493 312
$g.FillRectangle((New-Object System.Drawing.SolidBrush($navy)), 0, 0, 164, 312)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($teal)), 0, 0, 164, 4)
$g.DrawImage($markInverse, 24, 32, 56, 56)
$font = New-Object System.Drawing.Font('Segoe UI Semibold', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString($Product, $font, [System.Drawing.Brushes]::White, 22, 100)
$muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0xB9, 0xC0, 0xD6))
$g.DrawString($Tagline, $small, $muted, 23, 124)
$g.DrawString('СПб ГУП', $small, $muted, 23, 266)
$g.DrawString('«Горэлектротранс»', $small, $muted, 23, 281)
$bmp.Save((Join-Path $art 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()
$markColor.Dispose(); $markInverse.Dispose()
Write-Host "WixUI artwork written to $art"
