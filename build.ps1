# Builds "Monitor Dimmer.exe" with the C# compiler that ships with Windows.
# Usage: build.ps1 -Out <exe path> [-SelfTest]
param([Parameter(Mandatory)][string]$Out, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $u = $size / 32.0
    $g.FillRectangle([Drawing.Brushes]::Gray, 13 * $u, 23 * $u, 6 * $u, 4 * $u)
    $g.FillRectangle([Drawing.Brushes]::Gray, 9 * $u, 26 * $u, 14 * $u, 3 * $u)
    $g.FillRectangle([Drawing.Brushes]::Gray, 1 * $u, 4 * $u, 30 * $u, 20 * $u)
    $lit = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0, 120, 212))
    $g.FillRectangle($lit, 3 * $u, 6 * $u, 13 * $u, 16 * $u)
    $g.FillRectangle([Drawing.Brushes]::Black, 16 * $u, 6 * $u, 13 * $u, 16 * $u)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$iconPath = Join-Path $env:TEMP 'MonitorDimmer.ico'
$sizes = 16, 32, 48, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }
$w = New-Object IO.BinaryWriter ([IO.File]::Create($iconPath))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$Out", "/win32icon:$iconPath",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll')
if ($SelfTest) { $cscArgs += '/define:SELFTEST' }
& $csc @cscArgs (Join-Path $PSScriptRoot 'MonitorDimmer.cs')
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
