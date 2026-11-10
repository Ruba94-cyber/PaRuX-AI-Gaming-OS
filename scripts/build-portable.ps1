<#
.SYNOPSIS
    Builds the PaRuX portable ZIP (same product as the MSI, no separate app) and verifies it.

.DESCRIPTION
    Default ZIP: Desktop, Core, legal notices, the verified Vulkan engine, a standalone native CPU engine,
    local GGUF starter Chat model and local ONNX embeddings/reranker. No installed .NET, Windows App Runtime,
    Python, Ollama, LM Studio or external model directory is required for base offline Chat and document search.
    Build assets can be supplied by path or fetched ON THE BUILD MACHINE with -FetchAssets.
    Advanced media/training models remain optional; this does not bundle games, store clients or graphics drivers.
    The CLI is an optional separate self-contained publish (-IncludeCli), not part of the minimal default.
    Corporate Lite uses -WithoutVulkan and supplies its own CPU runtime and one model.

    Where data lives (no fixed paths, no duplication of the storage system):
      * PaRuX.exe                         -> the normal per-user profile, %LOCALAPPDATA%\PaRuX, one profile per PC.
                                             Nothing is carried between computers automatically.
      * "PaRuX (portable data).cmd"       -> sets PARUX_DATA_ROOT to a PaRuXData folder next to the program, so settings,
                                             models, history, RAG folders and indexes stay inside the portable folder.
    Both start the very same PaRuX.exe; the existing PARUX_DATA_ROOT resolver (PaRuXDataRoot) does the rest.

    The ZIP is not signed by this script and does nothing to bypass Windows SmartScreen, Defender or IT policy.
    The MSI-only protected update helper is not part of the portable package: update by downloading the new ZIP.

    Verification (always on) re-opens the ZIP, checks the runtime selection and required files, refuses forbidden payload
    (models, indexes, bootstrap tokens), and prints SHA-256. -Smoke additionally starts an extracted app against a
    throw-away data root and waits for Core plus the bootstrapped Vulkan runtime, then stops it.
    This does NOT claim that a particular GPU, ICD/driver, or model has successfully completed inference.

.EXAMPLE
    ./scripts/build-portable.ps1
    ./scripts/build-portable.ps1 -Smoke
    ./scripts/build-portable.ps1 -IncludeCli                 # additionally ship terminal CLI
    ./scripts/build-portable.ps1 -WithoutVulkan              # only for Corporate Lite's separate CPU pack
    ./scripts/build-portable.ps1 -SkipPublish -Smoke         # reuse an already staged matching variant
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$WithoutVulkan,
    [switch]$IncludeCli,
    [switch]$ShellOnly,
    [switch]$FetchAssets,
    [string]$ModelPath = "",
    [string]$CpuRuntimeDir = "",
    [string]$EmbeddingDir = "",
    [string]$RerankerDir = "",
    [string]$RuntimeTag = "",
    [switch]$Smoke,
    [int]$SmokeTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
# Standard Portable always carries a working local AI baseline. -ShellOnly is
# for internal diagnostics; -WithoutVulkan is the Corporate builder's CPU shell.
$fullyStandalone = -not $WithoutVulkan -and -not $ShellOnly
if ($WithoutVulkan -and $FetchAssets) { throw "-FetchAssets is for the full Standard Portable, not the Corporate CPU shell." }

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) { throw "The PaRuX portable package must be built on Windows." }

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj"
$coreProject = Join-Path $repoRoot "src\PaRuX.Server\PaRuX.Server.csproj"
$cliProject = Join-Path $repoRoot "src\PaRuX.Cli\PaRuX.Cli.csproj"
$portableRoot = Join-Path $repoRoot "artifacts\portable"
$stageDir = Join-Path $portableRoot "stage\PaRuX"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $Version = [string]($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch. Got '$Version'." }

$commitLines = @(& git -C $repoRoot rev-parse HEAD 2>$null)
$gitExit = $LASTEXITCODE
$sourceCommit = if ($gitExit -eq 0 -and $commitLines.Count -gt 0) { ([string]$commitLines[0]).Trim().ToLowerInvariant() } else { '' }
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') { throw "Could not resolve the Git source commit." }
$dirty = @(& git -C $repoRoot status --porcelain=v1 2>$null)
$dirtyNote = if ($dirty.Count -gt 0) { " (working tree has uncommitted changes: this is a local build, not a release artifact)" } else { "" }
Write-Host "Portable $Version from $sourceCommit$dirtyNote"

$zipName = "PaRuX-$Version-portable-win-x64.zip"
$zipPath = Join-Path $portableRoot $zipName
$shaPath = Join-Path $portableRoot "PaRuX-$Version-portable-win-x64.sha256.txt"

if (-not $SkipPublish) {
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    $fourPart = "$Version.0"
    $common = @("-p:Version=$Version", "-p:FileVersion=$fourPart", "-p:AssemblyVersion=$fourPart", "-p:InformationalVersion=$Version",
        "-p:SourceRevisionId=$sourceCommit", "-p:DebugSymbols=false", "-p:DebugType=None")

    Write-Host "Publishing PaRuX Desktop..."
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $stageDir $common
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed ($LASTEXITCODE)." }
    Write-Host "Publishing PaRuX Server (Core)..."
    & dotnet publish $coreProject -c Release -r win-x64 --self-contained true -o (Join-Path $stageDir "PaRuX.Server") $common
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed ($LASTEXITCODE)." }
    if ($IncludeCli) {
        Write-Host "Publishing optional PaRuX CLI..."
        & dotnet publish $cliProject -c Release -r win-x64 --self-contained true -o (Join-Path $stageDir "Cli") $common
        if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)." }
    }
    if (-not $WithoutVulkan) {
        # Reuse the MSI's pinned upstream ZIP, archive SHA/size gate, safe staging and
        # complete per-file payload manifest. No CUDA/cuBLAS/uv/Python is fetched.
        & (Join-Path $PSScriptRoot "stage-msi-runtime.ps1") -PublishDir $stageDir -ArtifactIds @("llama.cpp-vulkan")
        if (-not $?) { throw "Locked Vulkan runtime staging failed." }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $stageDir "PaRuX.exe"))) { throw "Nothing staged at $stageDir. Run without -SkipPublish first." }

foreach ($notice in @("LICENSE:LICENSE.txt", "NVIDIA_CUDA_NOTICE.txt:NVIDIA_CUDA_NOTICE.txt", "THIRD_PARTY_NOTICES.md:THIRD_PARTY_NOTICES.md", "OPEN_SOURCE_PROVENANCE.md:OPEN_SOURCE_PROVENANCE.md")) {
    $src, $dst = $notice.Split(':')
    if (Test-Path -LiteralPath (Join-Path $repoRoot $src)) { Copy-Item -LiteralPath (Join-Path $repoRoot $src) -Destination (Join-Path $stageDir $dst) -Force }
}

# A REAL portable package carries ALL durable state beside its own EXE. This
# applies to double-clicking PaRuX.exe, not merely a special batch-file launch.
if (-not $WithoutVulkan) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stageDir "PaRuXData") | Out-Null
    Set-Content -LiteralPath (Join-Path $stageDir "parux.portable") -Value "PaRuX Standard Portable" -Encoding ASCII
}
if ($fullyStandalone) {
    . (Join-Path $PSScriptRoot "portable-offline-assets.ps1")
    $offline = Resolve-PaRuXPortableOfflineAssets -AssetsRoot (Join-Path $repoRoot "artifacts\corporate-lite\assets") `
        -ModelPath $ModelPath -CpuRuntimeDir $CpuRuntimeDir -EmbeddingDir $EmbeddingDir `
        -RerankerDir $RerankerDir -RuntimeTag $RuntimeTag -Fetch:$FetchAssets
    $data = Join-Path $stageDir "PaRuXData"
    $cpuDest = Join-Path $data ("LocalAI\runtime\llama.cpp-" + $offline.RuntimeTag + "-cpu")
    New-Item -ItemType Directory -Force -Path $cpuDest | Out-Null
    Copy-Item -Path (Join-Path $offline.CpuRuntimeDir "*") -Destination $cpuDest -Recurse -Force
    $modelDest = Join-Path $data "models"
    New-Item -ItemType Directory -Force -Path $modelDest | Out-Null
    Copy-Item -LiteralPath $offline.ModelPath -Destination $modelDest -Force
    Set-Content -LiteralPath (Join-Path $modelDest ".portable-default-model") `
        -Value ([IO.Path]::GetFileName($offline.ModelPath)) -Encoding UTF8
    foreach ($assetType in @("embeddings", "reranker")) {
        $source = if ($assetType -eq "embeddings") { $offline.EmbeddingDir } else { $offline.RerankerDir }
        $destination = Join-Path $data ("ai-runtimes\" + $assetType)
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $cpuDest "llama-server.exe") -PathType Leaf)) {
        throw "Full Portable package is missing the actual native CPU llama-server.exe."
    }
}
# The batch file is only a convenient alternate entry point. Direct PaRuX.exe
# reads the same nearby PaRuXData directory from the explicit marker.
$cmd = @'
@echo off
rem Starts PaRuX with ALL of its data (settings, models, history, RAG folders, indexes) inside the "PaRuXData" folder
rem next to this file. Use the normal PaRuX.exe instead for a separate personal profile on each PC.
setlocal
set "PARUX_DATA_ROOT=%~dp0PaRuXData"
if not exist "%PARUX_DATA_ROOT%" mkdir "%PARUX_DATA_ROOT%"
start "" "%~dp0PaRuX.exe"
'@
Set-Content -LiteralPath (Join-Path $stageDir "PaRuX (portable data).cmd") -Value $cmd -Encoding ASCII

$readme = @"
PaRuX $Version - portable package
=================================
Unzip anywhere you are allowed to run programs, then start PaRuX.exe.

* PaRuX.exe                      Personal profile for THIS PC (%LOCALAPPDATA%\PaRuX). Each computer keeps its own settings,
                                 models, history, document folders and indexes. Nothing is copied between PCs.
* PaRuX (portable data).cmd      Keeps everything in the PaRuXData folder next to the program (settings, models, history,
                                 document folders, indexes). Copying the folder to another PC copies that data too.
$(if ($WithoutVulkan) { 'This CPU-only shell carries no Vulkan runtime; it must be completed by the Corporate Lite builder.' } else { 'A verified llama.cpp Vulkan runtime is bundled. Models are not included: choose a model on first launch. CUDA, Python and media engines install only on demand.' })
The optional CLI is $(if ($IncludeCli) { 'included' } else { 'not included' }); build with -IncludeCli if needed.
This package does not bypass Windows SmartScreen, antivirus or IT policy; if your policy blocks unsigned programs, use your
organisation's approved install route. To update, download the new portable ZIP (the MSI-only updater is not included).

PaRuX - pacchetto portable
Estrai in una cartella da cui puoi eseguire programmi e avvia PaRuX.exe (profilo personale di questo PC) oppure
"PaRuX (portable data).cmd" (tutti i dati nella cartella PaRuXData accanto al programma). Il runtime Vulkan
e incluso nella portable standard; modelli, CUDA, ComfyUI, Python e indici non sono inclusi. Il profilo Corporate Lite
include invece il proprio runtime CPU e un modello. Il pacchetto non aggira SmartScreen, antivirus o policy IT.
"@
Set-Content -LiteralPath (Join-Path $stageDir "README-portable.txt") -Value $readme -Encoding UTF8

New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host "Creating $zipName ..."
# Entries are written one by one with '/' separators under one top-level "PaRuX/" folder. ZipFile.CreateFromDirectory on
# Windows PowerShell 5 stores '\' in entry names, which is not valid ZIP and breaks 7-Zip and non-Windows unzip tools.
$zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $stageFull = (Resolve-Path -LiteralPath $stageDir).Path.TrimEnd('\')
        foreach ($file in (Get-ChildItem -LiteralPath $stageFull -Recurse -File)) {
            $relative = $file.FullName.Substring($stageFull.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, "PaRuX/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}
finally { $zipStream.Dispose() }
$sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $shaPath -Value "$sha  $zipName" -Encoding ASCII
$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "ZIP: $zipPath ($sizeMb MB)`nSHA-256: $sha"

# ---- Verify the finished ZIP -------------------------------------------------------------------------------------------
Write-Host "Verifying the ZIP..."
$required = @("PaRuX.exe", "PaRuX.pri", "PaRuX.Server/PaRuX.Server.exe", "PaRuX.Server/PaRuX.Server.dll",
    "LICENSE.txt", "README-portable.txt", "PaRuX (portable data).cmd")
if ($IncludeCli) { $required += "Cli/parux.exe" }
$baselineRelative = "PaRuX.Server/runtime-baseline"
$vulkanRelative = "$baselineRelative/llama.cpp-b10549-vulkan"
if (-not $WithoutVulkan) {
    $required += "$baselineRelative/baseline-lock.json"
    $required += "$baselineRelative/staging-manifest.json"
    $required += "$vulkanRelative/llama-server.exe"
}
$unwanted = @("PaRuX.Server/runtime-baseline/llama.cpp-b11871-cuda13.4/llama-server.exe",
    "PaRuX.Server/runtime-tools/uv/uv.exe")
$forbiddenPattern = '(?i)\.(gguf|safetensors|onnx|ckpt|pt|pth|bin\.gz)$|(^|/)rag\.db$|(^|/)bootstrap\.json$|^(models|LocalAI|PaRuXData)/'
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    # The ZIP has one top-level "PaRuX/" folder so extracting never scatters files into the current directory.
    $prefix = "PaRuX/"
    $backslashed = @($zip.Entries | Where-Object { $_.FullName.Contains('\') })
    if ($backslashed.Count -gt 0) { throw "Portable ZIP has $($backslashed.Count) entries with '\' separators (invalid ZIP): $($backslashed[0].FullName)" }
    $names = @($zip.Entries | ForEach-Object { if ($_.FullName.StartsWith($prefix)) { $_.FullName.Substring($prefix.Length) } else { $_.FullName } })
    foreach ($file in $required) { if ($names -notcontains $file) { throw "Portable ZIP is missing required file: $file" } }
    if (-not $IncludeCli -and @($names | Where-Object { $_ -like "Cli/*" }).Count -gt 0) {
        throw "Minimal portable ZIP unexpectedly contains the optional CLI."
    }
    if ($WithoutVulkan -and @($names | Where-Object { $_ -like "$baselineRelative/*" }).Count -gt 0) {
        throw "CPU-only shell unexpectedly contains a Vulkan/MSI runtime baseline."
    }
    foreach ($file in $unwanted) { if ($names -contains $file) { throw "Minimal portable ZIP contains an unwanted heavyweight runtime: $file" } }
    if (-not $WithoutVulkan) {
        # Recheck the actual ZIP bytes, not merely staging: every native DLL and EXE
        # must match the hash-locked upstream payload selected for this distribution.
        $lock = Get-Content -LiteralPath (Join-Path $stageDir "PaRuX.Server\runtime-baseline\baseline-lock.json") -Raw | ConvertFrom-Json
        $ids = @($lock.artifacts | ForEach-Object { [string]$_.id })
        if ($ids.Count -ne 1 -or $ids[0] -cne "llama.cpp-vulkan") {
            throw "Minimal portable must contain exactly the Vulkan runtime lock entry."
        }
        $manifest = Get-Content -LiteralPath (Join-Path $stageDir "PaRuX.Server\runtime-baseline\staging-manifest.json") -Raw | ConvertFrom-Json
        $entries = @($manifest.files | Where-Object { ([string]$_.path).StartsWith("runtime-baseline/llama.cpp-b10549-vulkan/") })
        if ($entries.Count -lt 2) { throw "Vulkan baseline manifest is missing native dependencies." }
        if (@($entries | Where-Object { $_.path -eq "runtime-baseline/llama.cpp-b10549-vulkan/ggml-vulkan.dll" }).Count -ne 1) {
            throw "The bundled Vulkan engine is missing ggml-vulkan.dll."
        }
        $inventory = @($manifest.files | Where-Object { ([string]$_.path).StartsWith("runtime-baseline/") })
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            foreach ($entry in $inventory) {
                $entryName = "PaRuX/PaRuX.Server/" + [string]$entry.path
                $zipEntry = $zip.GetEntry($entryName)
                if ($null -eq $zipEntry -or $zipEntry.Length -ne [int64]$entry.sizeBytes) {
                    throw "Manifested runtime payload is missing or has a different size: $entryName"
                }
                $stream = $zipEntry.Open()
                try {
                    $actualHash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
                }
                finally { $stream.Dispose() }
                if ($actualHash -cne ([string]$entry.sha256).ToLowerInvariant()) {
                    throw "Manifested runtime payload SHA-256 mismatch: $entryName"
                }
            }
        }
        finally { $sha256.Dispose() }
        if (@($names | Where-Object { $_ -like "PaRuX.Server/runtime-baseline/llama.cpp-*-cuda*/*" -or $_ -like "PaRuX.Server/runtime-tools/uv/*" }).Count -gt 0) {
            throw "Minimal portable contains unwanted CUDA or uv runtime files."
        }
    }
    $bad = @($names | Where-Object { $_ -match $forbiddenPattern -and $_ -notmatch '(?i)(^|/)(Microsoft\.|System\.|onnxruntime|DirectML)' })
    if ($bad.Count -gt 0) { throw "Portable ZIP contains payload that must not ship (models/indexes/tokens): $($bad | Select-Object -First 5 | Out-String)" }
    Write-Host "ZIP check OK: $($names.Count) entries, required files present, no models, indexes or tokens."
}
finally { $zip.Dispose() }

if ($Smoke) {
    $smokeDir = Join-Path ([IO.Path]::GetTempPath()) ("parux-portable-smoke-" + [Guid]::NewGuid().ToString('N'))
    $extract = Join-Path $smokeDir "app"
    $data = Join-Path $smokeDir "data"
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extract)
    $env:PARUX_DATA_ROOT = $data
    $env:PARUX_LOCALAI_STATE_DIR = $data
    $proc = Start-Process -FilePath (Join-Path $extract "PaRuX\PaRuX.exe") -PassThru
    try {
        $deadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
        $bootstrap = Join-Path $data "server\bootstrap.json"
        while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $bootstrap)) {
            if ($proc.HasExited) { throw "PaRuX.exe exited early (code $($proc.ExitCode)) during the portable smoke start." }
            Start-Sleep -Seconds 2
        }
        if (-not (Test-Path -LiteralPath $bootstrap)) { throw "Core did not publish bootstrap.json within $SmokeTimeoutSeconds s." }
        if (-not $WithoutVulkan) {
            $managedVulkan = Join-Path $data "LocalAI\runtime\llama.cpp-b10549-vulkan\llama-server.exe"
            if (-not (Test-Path -LiteralPath $managedVulkan -PathType Leaf)) {
                $errorPath = Join-Path $data "baseline-bootstrap.error.txt"
                $detail = if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath -Raw } else { 'No bootstrap error file.' }
                throw "Portable Core started but did not bootstrap the bundled Vulkan runtime into its isolated data root. $detail"
            }

            # Binary load/linked-DLL smoke: prevents treating a copied llama-server.exe as usable
            # while ggml-vulkan.dll or other native dependencies fail to load. A real model
            # inference and device/ICD check still requires separate physical Windows acceptance.
            $nativeOut = Join-Path $smokeDir "vulkan-help.stdout.txt"
            $nativeErr = Join-Path $smokeDir "vulkan-help.stderr.txt"
            $native = Start-Process -FilePath $managedVulkan -ArgumentList "--help" -WorkingDirectory (Split-Path -Parent $managedVulkan) -NoNewWindow -PassThru -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
            try {
                if (-not $native.WaitForExit(15000)) {
                    try { $native.Kill() } catch { }
                    throw "Bundled Vulkan llama-server --help did not exit within 15 seconds."
                }
                if ($native.ExitCode -ne 0) {
                    $errorText = if (Test-Path -LiteralPath $nativeErr) { (Get-Content -LiteralPath $nativeErr -Raw).Trim() } else { "" }
                    throw "Bundled Vulkan llama-server failed its native loader probe (exit $($native.ExitCode)): $errorText"
                }
            }
            finally { $native.Dispose() }
        }
        Write-Host "Smoke OK: extracted portable Core started$(if ($WithoutVulkan) { '' } else { ' and Vulkan native loader passed' }). Real GPU inference still requires a model/driver test."
    }
    finally {
        Get-Process -Name PaRuX, PaRuX.Server -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($extract, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item Env:PARUX_DATA_ROOT, Env:PARUX_LOCALAI_STATE_DIR -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Done."
