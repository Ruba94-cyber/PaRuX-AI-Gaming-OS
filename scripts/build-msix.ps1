<#
.SYNOPSIS
    Builds the Microsoft Store (MSIX, full-trust packaged desktop app) edition of PaRuX. Does not touch the MSI/Portable builders.

.DESCRIPTION
    Same product as the MSI/Portable (Desktop + Core), published the same way (`dotnet publish`, self-contained .NET and
    Windows App SDK), laid out as an MSIX package:
      * the Store IDENTITY (Name, Publisher, PublisherDisplayName) comes from a Git-ignored local JSON file (-IdentityFile);
        it is never written to a tracked file. Required keys: identityName, identityPublisher, publisherDisplayName.
        Optional: appDisplayName (must equal the name reserved in Partner Center; default "PaRuX AI Studio").
      * AppxManifest.xml is generated from packaging/msix/AppxManifest.template.xml (runFullTrust, internet / LAN capabilities).
      * Logo assets are derived from the repository's approved PRX Orbit artwork (src/PaRuX.App/Assets); nothing new is invented.
      * The package carries the pinned baseline Vulkan llama.cpp runtime (same hash-locked staging as the MSI/Portable) so base
        Chat can start, but NO model weights: models are downloaded on user action by the Model Hub.
      * Not in the package: PaRuX.UpdateHelper / the MSI self-updater (the Store updates the app), the CLI, the portable
        marker, tokens or user data. The app detects package identity at runtime and disables in-app self-update itself.
      * The package version is major.minor.patch.0 (the Store reserves the 4th part).
    The output .msix is UNSIGNED: Partner Center re-signs MSIX submissions. -SignTestCopy additionally signs a COPY with a
    throwaway self-signed certificate (CurrentUser store, subject = manifest Publisher) so a local sideload test is possible;
    installing it needs that certificate trusted machine-wide, which this script never does.

.EXAMPLE
    ./scripts/build-msix.ps1 -IdentityFile "<main checkout>\.ai\store\store-identity.local.json" -RuntimeCacheDir "<main checkout>\artifacts\cache\runtime"
#>
[CmdletBinding()]
param(
    [string]$IdentityFile = "",
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$WithoutBaselineRuntime,
    [string]$RuntimeCacheDir = "",
    [string]$OutputDir = "",
    [string]$MakeAppxPath = "",
    [switch]$SignTestCopy
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) { throw "The PaRuX MSIX package must be built on Windows." }

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj"
$coreProject = Join-Path $repoRoot "src\PaRuX.Server\PaRuX.Server.csproj"
$templatePath = Join-Path $repoRoot "packaging\msix\AppxManifest.template.xml"
$msixRoot = if ([string]::IsNullOrWhiteSpace($OutputDir)) { Join-Path $repoRoot "artifacts\msix" } else { $OutputDir }
$stageDir = Join-Path $msixRoot "stage\layout"

# ---- identity (never tracked) -------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($IdentityFile)) {
    throw "-IdentityFile is required. Pass the Git-ignored Store identity file (keys identityName, identityPublisher, publisherDisplayName), e.g. '<main checkout>\.ai\store\store-identity.local.json'. See docs/store/SUBMISSION_CHECKLIST.md."
}
if (-not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) {
    throw "Store identity file not found: '$IdentityFile'. Reserve the app name in Partner Center, then copy Product identity (Name, Publisher, Publisher display name) into this Git-ignored file. See docs/store/SUBMISSION_CHECKLIST.md."
}
$identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
foreach ($key in @("identityName", "identityPublisher", "publisherDisplayName")) {
    if (-not ($identity.PSObject.Properties.Name -contains $key) -or [string]::IsNullOrWhiteSpace([string]$identity.$key)) {
        throw "Store identity file is missing a non-empty '$key'."
    }
}
$identityName = [string]$identity.identityName
$identityPublisher = [string]$identity.identityPublisher
$publisherDisplayName = [string]$identity.publisherDisplayName
$appDisplayName = if (($identity.PSObject.Properties.Name -contains "appDisplayName") -and -not [string]::IsNullOrWhiteSpace([string]$identity.appDisplayName)) { [string]$identity.appDisplayName } else { "PaRuX AI Studio" }
if ($identityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]{2,49}$') { throw "identityName is not a valid package identity name." }
if ($identityPublisher -notmatch '^CN=.+') { throw "identityPublisher must be the distinguished name copied from Partner Center (starts with 'CN=')." }

# ---- version ------------------------------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $Version = [string]($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch (the Store reserves the 4th part, which this script sets to 0). Got '$Version'." }
$packageVersion = "$Version.0"

# ---- tools --------------------------------------------------------------------------------------------------------
function Find-SdkTool([string]$name, [string]$explicit) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        if (-not (Test-Path -LiteralPath $explicit -PathType Leaf)) { throw "$name not found at '$explicit'." }
        return $explicit
    }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $found = @(Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($found.Count -eq 0) { throw "$name not found. Install the Windows 10/11 SDK (Windows Kits\10\bin\<version>\x64)." }
    return $found[0]
}
$makeAppx = Find-SdkTool "makeappx.exe" $MakeAppxPath
$makePri = Find-SdkTool "makepri.exe" ""
Write-Host "makeappx: $makeAppx"

$commitLines = @(& git -C $repoRoot rev-parse HEAD 2>$null)
$sourceCommit = if ($LASTEXITCODE -eq 0 -and $commitLines.Count -gt 0) { ([string]$commitLines[0]).Trim().ToLowerInvariant() } else { '' }
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Could not resolve the Git source commit." }
$dirty = @(& git -C $repoRoot status --porcelain=v1 2>$null)
$dirtyNote = if ($dirty.Count -gt 0) { " (uncommitted changes: local build, not a submission artifact)" } else { "" }
Write-Host "MSIX $packageVersion from $sourceCommit$dirtyNote"

# ---- publish (same steps as build-portable.ps1 / build-msi.ps1) ---------------------------------------------------
if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    $common = @("-p:Version=$Version", "-p:FileVersion=$packageVersion", "-p:AssemblyVersion=$packageVersion", "-p:InformationalVersion=$Version",
        "-p:SourceRevisionId=$sourceCommit", "-p:DebugSymbols=false", "-p:DebugType=None")
    Write-Host "Publishing PaRuX Desktop..."
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $stageDir $common
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed ($LASTEXITCODE)." }
    Write-Host "Publishing PaRuX Server (Core)..."
    & dotnet publish $coreProject -c Release -r win-x64 --self-contained true -o (Join-Path $stageDir "PaRuX.Server") $common
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed ($LASTEXITCODE)." }
    if (-not $WithoutBaselineRuntime) {
        $stageArgs = @{ PublishDir = $stageDir; ArtifactIds = @("llama.cpp-vulkan") }
        if (-not [string]::IsNullOrWhiteSpace($RuntimeCacheDir)) { $stageArgs.CacheDir = $RuntimeCacheDir }
        & (Join-Path $PSScriptRoot "stage-msi-runtime.ps1") @stageArgs
        if (-not $?) { throw "Locked Vulkan runtime staging failed." }
        # llama.cpp imports the Visual C++ runtime; ship it app-local exactly like the portable editions.
        . (Join-Path $PSScriptRoot "portable-offline-assets.ps1")
        Copy-PaRuXVcRuntime -Destinations @($stageDir, (Join-Path $stageDir "PaRuX.Server")) | Out-Null
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $stageDir "PaRuX.exe") -PathType Leaf)) { throw "Nothing staged at $stageDir. Run without -SkipPublish first." }
if (-not (Test-Path -LiteralPath (Join-Path $stageDir "PaRuX.Server\PaRuX.Server.exe") -PathType Leaf)) { throw "Core is missing from the layout." }

foreach ($notice in @("LICENSE:LICENSE.txt", "NVIDIA_CUDA_NOTICE.txt:NVIDIA_CUDA_NOTICE.txt", "THIRD_PARTY_NOTICES.md:THIRD_PARTY_NOTICES.md", "OPEN_SOURCE_PROVENANCE.md:OPEN_SOURCE_PROVENANCE.md")) {
    $src, $dst = $notice.Split(':')
    if (Test-Path -LiteralPath (Join-Path $repoRoot $src)) { Copy-Item -LiteralPath (Join-Path $repoRoot $src) -Destination (Join-Path $stageDir $dst) -Force }
}

# ---- things that must NOT be in the Store package ------------------------------------------------------------------
foreach ($forbidden in @("parux.portable", "PaRuXData", "UpdateHelper", "Cli", "PaRuX.UpdateHelper.exe", "README-portable.txt", ".parux-publish-source")) {
    $path = Join-Path $stageDir $forbidden
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
Get-ChildItem -LiteralPath $stageDir -Recurse -File -Include *.pdb | Remove-Item -Force
$bigModels = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File -Include *.gguf, *.safetensors, *.onnx | Where-Object { $_.Length -gt 50MB })
if ($bigModels.Count -gt 0) { throw "Model weights must not be bundled in the Store package: $($bigModels[0].FullName)" }

# ---- logo assets from the approved branding -----------------------------------------------------------------------
Add-Type -AssemblyName System.Drawing
$assetsDir = Join-Path $stageDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$logoSource = Join-Path $repoRoot "src\PaRuX.App\Assets\prx-orbit-approved.png"
if (-not (Test-Path -LiteralPath $logoSource -PathType Leaf)) { throw "Approved branding asset missing: $logoSource" }
function New-LogoPng([string]$source, [string]$destination, [int]$width, [int]$height) {
    $img = [System.Drawing.Image]::FromFile($source)
    try {
        $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                # Fit the square artwork inside the target keeping aspect ratio, centred (wide tile gets side padding).
                $side = [Math]::Min($width, $height)
                $x = [int](($width - $side) / 2); $y = [int](($height - $side) / 2)
                $g.DrawImage($img, $x, $y, $side, $side)
            } finally { $g.Dispose() }
            $bmp.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bmp.Dispose() }
    } finally { $img.Dispose() }
}
$logos = [ordered]@{
    "Square44x44Logo.png" = @(44, 44); "Square44x44Logo.scale-200.png" = @(88, 88)
    "Square44x44Logo.targetsize-256_altform-unplated.png" = @(256, 256)
    "Square150x150Logo.png" = @(150, 150); "Square150x150Logo.scale-200.png" = @(300, 300)
    "Wide310x150Logo.png" = @(310, 150); "Wide310x150Logo.scale-200.png" = @(620, 300)
    "SmallTile.png" = @(71, 71); "LargeTile.png" = @(310, 310); "StoreLogo.png" = @(50, 50)
}
foreach ($name in $logos.Keys) { New-LogoPng $logoSource (Join-Path $assetsDir $name) $logos[$name][0] $logos[$name][1] }

# ---- manifest -----------------------------------------------------------------------------------------------------
function Escape-Xml([string]$s) { [System.Security.SecurityElement]::Escape($s) }
$manifest = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
$manifest = $manifest.Replace("@@IDENTITY_NAME@@", (Escape-Xml $identityName)).Replace("@@IDENTITY_PUBLISHER@@", (Escape-Xml $identityPublisher)).
    Replace("@@PUBLISHER_DISPLAY_NAME@@", (Escape-Xml $publisherDisplayName)).Replace("@@APP_DISPLAY_NAME@@", (Escape-Xml $appDisplayName)).
    Replace("@@VERSION@@", $packageVersion).Replace("@@EXTENSIONS@@", "")
if ($manifest -match '@@[A-Z_]+@@') { throw "Unreplaced manifest placeholder: $($Matches[0])" }
$manifestPath = Join-Path $stageDir "AppxManifest.xml"
[xml]$null = $manifest   # throws on malformed XML
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# ---- resources.pri (a packaged app resolves ms-appx / XBF resources through <package root>\resources.pri) -----------
$priConfig = Join-Path $msixRoot "priconfig.xml"
if (Test-Path -LiteralPath (Join-Path $stageDir "resources.pri")) { Remove-Item -LiteralPath (Join-Path $stageDir "resources.pri") -Force }
& $makePri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed ($LASTEXITCODE)." }
# Merge the build's existing PRI files (PaRuX.pri, Microsoft.UI*.pri, ...) and the logo assets into ONE primary resource map
# named after the package identity. Indexing the whole layout instead would see the XBF/asset files twice (loose + inside the
# PRIs) and fail with a duplicate-resource error, so only the PRIs and Assets are indexed from a scratch folder.
$priWork = Join-Path $msixRoot "pri-work"
if (Test-Path -LiteralPath $priWork) { Remove-Item -LiteralPath $priWork -Recurse -Force }
New-Item -ItemType Directory -Force -Path $priWork | Out-Null
Get-ChildItem -LiteralPath $stageDir -File -Filter *.pri | Copy-Item -Destination $priWork
Copy-Item -LiteralPath $assetsDir -Destination (Join-Path $priWork "Assets") -Recurse
Copy-Item -LiteralPath $manifestPath -Destination $priWork
& $makePri new /pr $priWork /cf $priConfig /mn (Join-Path $priWork "AppxManifest.xml") /of (Join-Path $priWork "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $priWork "resources.pri") -Destination (Join-Path $stageDir "resources.pri") -Force
Remove-Item -LiteralPath $priWork -Recurse -Force

# ---- pack ---------------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $msixRoot | Out-Null
$msixPath = Join-Path $msixRoot ("PaRuX_{0}_x64.msix" -f $packageVersion)
if (Test-Path -LiteralPath $msixPath) { Remove-Item -LiteralPath $msixPath -Force }
& $makeAppx pack /d $stageDir /p $msixPath /o | Select-String -Pattern "error|warning|succeeded" | ForEach-Object { Write-Host $_.Line }
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

# ---- verify: re-open the package and check the manifest actually inside it -----------------------------------------
$verifyDir = Join-Path $msixRoot "verify-unpack"
if (Test-Path -LiteralPath $verifyDir) { Remove-Item -LiteralPath $verifyDir -Recurse -Force }
& $makeAppx unpack /p $msixPath /d $verifyDir /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx unpack (verification) failed ($LASTEXITCODE)." }
[xml]$packed = Get-Content -LiteralPath (Join-Path $verifyDir "AppxManifest.xml") -Raw
$packedVersion = $packed.Package.Identity.Version
if ($packedVersion -ne $packageVersion -or -not $packedVersion.EndsWith(".0")) { throw "Packed version '$packedVersion' is not $packageVersion." }
foreach ($required in @("PaRuX.exe", "PaRuX.Server\PaRuX.Server.exe", "resources.pri", "Assets\StoreLogo.png", "Assets\Square150x150Logo.png", "Assets\Square44x44Logo.png")) {
    if (-not (Test-Path -LiteralPath (Join-Path $verifyDir $required))) { throw "Package is missing $required." }
}
if (Test-Path -LiteralPath (Join-Path $verifyDir "parux.portable")) { throw "Package contains the portable marker." }
Remove-Item -LiteralPath $verifyDir -Recurse -Force

$hash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($msixPath + ".sha256.txt") -Value "$hash  $(Split-Path -Leaf $msixPath)" -Encoding ASCII
$sizeMb = [Math]::Round((Get-Item -LiteralPath $msixPath).Length / 1MB, 1)
Write-Host "MSIX built: $msixPath ($sizeMb MB), SHA-256 $hash (UNSIGNED: Partner Center re-signs Store submissions)"

# ---- optional: signed COPY for local sideload tests ---------------------------------------------------------------
if ($SignTestCopy) {
    $signTool = Find-SdkTool "signtool.exe" ""
    $cert = New-SelfSignedCertificate -Type Custom -Subject $identityPublisher -KeyUsage DigitalSignature -FriendlyName "PaRuX MSIX test (throwaway)" `
        -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    $testCopy = Join-Path $msixRoot ("PaRuX_{0}_x64.TESTSIGNED.msix" -f $packageVersion)
    Copy-Item -LiteralPath $msixPath -Destination $testCopy -Force
    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $testCopy
    if ($LASTEXITCODE -ne 0) { throw "signtool sign (test copy) failed ($LASTEXITCODE)." }
    Write-Host "Test-signed copy: $testCopy (certificate thumbprint $($cert.Thumbprint) in CurrentUser\My; NOT trusted anywhere - installing it needs an elevated trust step this script never performs)."
}
