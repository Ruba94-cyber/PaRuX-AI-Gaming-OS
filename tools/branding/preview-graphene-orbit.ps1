#Requires -Version 5.1
<#
.SYNOPSIS
    Creates NON-DESTRUCTIVE graphene colour studies from the already-approved PRX Orbit bitmap.
.DESCRIPTION
    Keeps geometry, alpha, and neutral metallic highlights. Replaces saturated blue/violet pixels
    with titanium grey at a corresponding luminance. Writes only to a preview folder, NEVER into
    the active app/tray/installer resources. Review 16/20/24/32/48/64/128/256 px before approval.
    This is a candidate generator, not an automatic approval or replacement of the brand asset.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'This System.Drawing preview generator is supported only on Windows.'
}
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'artifacts/branding/graphene-preview'
}
$sourcePath = Join-Path $RepositoryRoot 'src/PaRuX.App/Assets/prx-orbit-approved.png'
if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Approved asset not found: $sourcePath" }
# Never permit output to be the asset directory or a path nested under it.
$assetDirectory = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'src/PaRuX.App/Assets'))
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($outputFull.TrimEnd([char]92, [char]47) -eq $assetDirectory.TrimEnd([char]92, [char]47) -or
    $outputFull.StartsWith($assetDirectory.TrimEnd([char]92, [char]47) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to write a branding preview under the shipped Assets directory.'
}
New-Item -ItemType Directory -Force -Path $outputFull | Out-Null

function Get-GreyPixel([System.Drawing.Color]$pixel) {
    if ($pixel.A -eq 0) { return [System.Drawing.Color]::Transparent }
    $maximum = [Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B))
    $minimum = [Math]::Min($pixel.R, [Math]::Min($pixel.G, $pixel.B))
    $saturation = if ($maximum -eq 0) { 0.0 } else { ($maximum - $minimum) / [double]$maximum }
    # Preserve existing neutral metal, white lettering, dark contours and alpha.
    if ($saturation -lt 0.18 -or $maximum -lt 28) { return $pixel }
    $luma = (0.2126 * $pixel.R + 0.7152 * $pixel.G + 0.0722 * $pixel.B) / 255.0
    # Equal-luminance-ish graphite-to-titanium ramp; no gold/neon accent in the icon.
    $level = [Math]::Min(1.08, 0.67 + 0.58 * $luma)
    $red   = [int][Math]::Round(183 * $level)
    $green = [int][Math]::Round(192 * $level)
    $blue  = [int][Math]::Round(201 * $level)
    return [System.Drawing.Color]::FromArgb($pixel.A, $red, $green, $blue)
}

function Export-ScaledPng {
    param([System.Drawing.Bitmap]$Source, [int]$Size, [string]$Path, [string]$Background = '')
    $dest = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dest)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        if ($Background) { $g.Clear([System.Drawing.ColorTranslator]::FromHtml($Background)) }
        $factor = [Math]::Min($Size / [double]$Source.Width, $Size / [double]$Source.Height)
        $w = [int][Math]::Round($Source.Width * $factor)
        $h = [int][Math]::Round($Source.Height * $factor)
        $destRect = [System.Drawing.Rectangle]::new([int][Math]::Floor(($Size-$w)/2),
            [int][Math]::Floor(($Size-$h)/2), $w, $h)
        $g.DrawImage($Source, $destRect)
        $dest.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $g.Dispose(); $dest.Dispose() }
}

$source = [System.Drawing.Bitmap]::new($sourcePath)
$preview = [System.Drawing.Bitmap]::new($source.Width, $source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            $pixel = $source.GetPixel($x, $y)
            $converted = Get-GreyPixel -pixel $pixel
            $preview.SetPixel($x, $y, $converted)
        }
    }
    $master = Join-Path $outputFull 'prx-orbit-graphene-candidate.png'
    $preview.Save($master, [System.Drawing.Imaging.ImageFormat]::Png)
    foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
        Export-ScaledPng -Source $preview -Size $size -Path (Join-Path $outputFull ("graphene-{0}px.png" -f $size))
    }
    Export-ScaledPng -Source $preview -Size 512 -Path (Join-Path $outputFull 'graphene-on-dark.png') -Background '#07090C'
    Export-ScaledPng -Source $preview -Size 512 -Path (Join-Path $outputFull 'graphene-on-light.png') -Background '#F5F6F8'
}
finally {
    $preview.Dispose()
    $source.Dispose()
}
Write-Output "Graphene candidate previews created in $outputFull. Existing packaged icons and approved artwork were NOT modified."
