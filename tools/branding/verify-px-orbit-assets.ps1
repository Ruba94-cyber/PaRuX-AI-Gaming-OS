<#
.SYNOPSIS
    Read-only PX Orbit source and publish artifact integrity check (Windows PowerShell 5.1+).
.DESCRIPTION
    Validates PNG dimensions, all 8 embedded ICO PNG frames, distinct app/tray images,
    WinUI/Android resource wiring and optional published bytes. No network, installers,
    downloads, GitHub Actions or extra developer dependencies.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$PublishDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assets = Join-Path $RepositoryRoot 'src\PaRuX.App\Assets'

function Assert-True([bool]$Ok, [string]$Message) {
    if (-not $Ok) { throw "PX Orbit asset gate: $Message" }
}

function Read-Bytes([string]$Path) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Missing file: $Path"
    return ,([System.IO.File]::ReadAllBytes($Path))
}

function Read-PngUInt32([byte[]]$Bytes, [int]$Offset) {
    return (([uint32]$Bytes[$Offset] -shl 24) -bor
            ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
            ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
            [uint32]$Bytes[$Offset + 3])
}

function Assert-Png([byte[]]$Bytes, [int]$Offset, [int]$End, [int]$ExpectedSize) {
    $signature = @(137,80,78,71,13,10,26,10)
    Assert-True (($End - $Offset) -ge 33) 'PNG truncated before IHDR.'
    for ($i = 0; $i -lt $signature.Count; $i++) {
        Assert-True ($Bytes[$Offset + $i] -eq $signature[$i]) 'Invalid PNG signature.'
    }
    Assert-True (([char]$Bytes[$Offset + 12]) -eq 'I' -and
        ([char]$Bytes[$Offset + 13]) -eq 'H' -and
        ([char]$Bytes[$Offset + 14]) -eq 'D' -and
        ([char]$Bytes[$Offset + 15]) -eq 'R') 'PNG has no IHDR.'
    $width = Read-PngUInt32 -Bytes $Bytes -Offset ($Offset + 16)
    $height = Read-PngUInt32 -Bytes $Bytes -Offset ($Offset + 20)
    Assert-True ($width -eq $ExpectedSize -and $height -eq $ExpectedSize) "PNG dimensions $width x $height are not $ExpectedSize x $ExpectedSize."
}

$expectedSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
function Assert-Ico([string]$Path) {
    [byte[]]$bytes = Read-Bytes $Path
    Assert-True ($bytes.Length -ge (6 + 16 * $expectedSizes.Count)) "ICO table is truncated: $Path"
    Assert-True (([BitConverter]::ToUInt16($bytes, 0)) -eq 0) 'ICO reserved header must be zero.'
    Assert-True (([BitConverter]::ToUInt16($bytes, 2)) -eq 1) 'ICO header must specify icon type.'
    $frameCount = [int][BitConverter]::ToUInt16($bytes, 4)
    Assert-True ($frameCount -eq $expectedSizes.Count) "ICO frame count must be 8: $Path"
    for ($i = 0; $i -lt $expectedSizes.Count; $i++) {
        $at = 6 + 16 * $i
        $size = $expectedSizes[$i]
        $encoded = if ($size -eq 256) { 0 } else { $size }
        Assert-True ($bytes[$at] -eq $encoded -and $bytes[$at + 1] -eq $encoded) "ICO frame $i has wrong dimensions: $Path"
        $length = [BitConverter]::ToUInt32($bytes, $at + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $at + 12)
        $end = [long]$offset + [long]$length
        Assert-True ($offset -ge (6 + 16 * $expectedSizes.Count) -and $end -le $bytes.Length) "ICO frame $i extends beyond payload: $Path"
        Assert-Png -Bytes $bytes -Offset ([int]$offset) -End ([int]$end) -ExpectedSize $size
    }
}

$appPng = Join-Path $assets 'px-orbit-app.png'
$chatPng = Join-Path $assets 'px-orbit-chat.png'
$appIco = Join-Path $assets 'parux.ico'
$trayIco = Join-Path $assets 'prx-orbit-tray.ico'
[byte[]]$app = Read-Bytes $appPng
[byte[]]$chat = Read-Bytes $chatPng
Assert-Png -Bytes $app -Offset 0 -End $app.Length -ExpectedSize 256
Assert-Png -Bytes $chat -Offset 0 -End $chat.Length -ExpectedSize 256
Assert-Ico $appIco
Assert-Ico $trayIco
# Owner decision 2026-11-03: the Quick Chat bubble shows the approved app mark (same artwork, bubble styling unchanged).
Assert-True ((Get-FileHash -LiteralPath $appPng -Algorithm SHA256).Hash -eq
    (Get-FileHash -LiteralPath $chatPng -Algorithm SHA256).Hash) 'The chat bubble must use the approved app icon artwork.'

$theme = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\PaRuX.App\Styles\Theme.xaml') -Raw
$bubble = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\PaRuX.App\Windows\BubbleWindow.xaml') -Raw
$code = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\PaRuX.App\Windows\BubbleWindow.xaml.cs') -Raw
$project = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\PaRuX.App\PaRuX.App.csproj') -Raw
$generator = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'tools\branding\generate-prx-assets.ps1') -Raw
Assert-True ($theme.Contains('ms-appx:///Assets/px-orbit-app.png')) 'Shell mark is not PX Orbit.'
Assert-True ($theme.Contains('ms-appx:///Assets/px-orbit-chat.png')) 'The retained companion/tray artwork is missing.'
Assert-True (-not $theme.Contains('ms-appx:///Assets/prx-orbit-approved.png')) 'Legacy shell artwork was reintroduced.'
Assert-True (($bubble.Contains('ContentTemplate="{StaticResource PrxOrbitMarkTemplate}"')) -and
    (-not $bubble.Contains('ContentTemplate="{StaticResource PxOrbitChatMarkTemplate}"')) -and
    ($bubble.Contains('StrokeDashArray="1,2"')) -and
    ($bubble.Contains('<RotateTransform x:Name="CollapsedStateRingRotation" Angle="-25" />')) -and
    ($code.Contains('Storyboard.SetTarget(orbit, CollapsedStateRingRotation)')) -and
    ($code.Contains('Storyboard.SetTargetProperty(orbit, "Angle")'))) 'The original Quick Chat pill/orbit style was changed.'
Assert-True (($code.Contains('AreAnimationsEnabled()')) -and
    ($code.Contains('if (!_visible || _expanded) return;'))) 'Reduced-motion or hidden-window safety is missing.'
foreach ($file in @('px-orbit-app.png','px-orbit-chat.png','parux.ico','prx-orbit-tray.ico')) {
    Assert-True ($project.Contains("Assets\$file")) "Desktop package does not include $file."
}
Assert-True ($generator.Contains('$chatLogoPath')) 'Tray generator no longer uses the distinct chat source.'
Assert-True ($generator.Contains('$trayIconPath -Sizes @(16, 20, 24, 32, 48, 64, 128, 256)')) 'Tray generator lost one or more icon sizes.'

$android = Join-Path $RepositoryRoot 'src\PaRuX.Android\app\src\main\res'
$androidPng = Join-Path $android 'drawable-nodpi\prx_orbit_approved.png'
Assert-True ((Get-FileHash -LiteralPath $androidPng -Algorithm SHA256).Hash -eq
    (Get-FileHash -LiteralPath $appPng -Algorithm SHA256).Hash) 'Android launcher artwork does not match the approved Windows icon.'
foreach ($path in @('drawable\ic_launcher_foreground.xml', 'drawable\ic_launcher_monochrome.xml')) {
    $contents = Get-Content -LiteralPath (Join-Path $android $path) -Raw
    Assert-True ($contents.Contains('android:insetLeft="20dp"')) "Android safe-zone inset missing: $path"
    $null = [xml]$contents
}
foreach ($path in @('mipmap-anydpi-v26\ic_launcher.xml','mipmap-anydpi-v26\ic_launcher_round.xml')) {
    $contents = Get-Content -LiteralPath (Join-Path $android $path) -Raw
    Assert-True ($contents.Contains('@drawable/ic_launcher_monochrome')) "Android monochrome launcher missing: $path"
    $null = [xml]$contents
}

if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
    foreach ($name in @('px-orbit-app.png','px-orbit-chat.png','parux.ico','prx-orbit-tray.ico')) {
        $source = Join-Path $assets $name
        $published = Join-Path $publishRoot "Assets\$name"
        Assert-True (Test-Path -LiteralPath $published -PathType Leaf) "Published Windows asset missing: $name"
        Assert-True ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) "Published Windows asset hash mismatch: $name"
    }
}

Write-Host '[PX Orbit] source, 8-size icons, Android launcher and optional published assets: PASS'
