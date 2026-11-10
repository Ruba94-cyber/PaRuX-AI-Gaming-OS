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
      * PaRuX.exe                         -> detects parux.portable and PaRuXData beside the EXE.
                                             App, Core and AI state stay inside this folder after relocation.
      * "PaRuX (portable data).cmd"       -> optional shortcut; identical data location.
    MSI editions have neither the portable marker nor an adjacent PaRuXData directory, so
    they keep the original per-user %LOCALAPPDATA%\PaRuX behavior.

    The ZIP is not signed by this script and does nothing to bypass Windows SmartScreen, Defender or IT policy.
    The MSI-only protected update helper is not part of the portable package: update by downloading the new ZIP.

    Verification (always on) re-opens the ZIP, checks the runtime selection and required files, refuses forbidden payload
    (tokens, user files and indexes), verifies the full offline model/ONNX/native file inventory,
    and prints SHA-256. -Smoke starts a relocated extracted EXE with environment overrides cleared,
    checks Core, native CPU/Vulkan loader and ONNX load; real CPU/driver inference still needs
    the separate clean-machine acceptance test.

.EXAMPLE
    ./scripts/build-portable.ps1 -FetchAssets -Smoke    # complete Standard Offline ZIP
    ./scripts/build-portable.ps1 -Smoke                # offline from locally cached reviewed assets
    ./scripts/build-portable.ps1 -ShellOnly            # incomplete development shell only
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
# Different editions must NEVER share a mutable stage directory.
# In particular, Corporate's CPU-only staging must not overwrite the signed
# Standard ZIP or its verified publish tree during a multi-package release.
$stageDir = if ($WithoutVulkan) { Join-Path $portableRoot "cpu-shell-stage\PaRuX" } elseif ($ShellOnly) {
    Join-Path $portableRoot "diagnostic-shell-stage\PaRuX"
} else { Join-Path $portableRoot "stage\PaRuX" }

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

$zipBase = if ($WithoutVulkan) { "PaRuX-$Version-CPU-shell-INTERNAL-win-x64" } elseif ($ShellOnly) {
    "PaRuX-$Version-diagnostic-shell-INTERNAL-win-x64"
} else { "PaRuX-$Version-portable-win-x64" }
$zipName = $zipBase + ".zip"
$zipPath = Join-Path $portableRoot $zipName
$shaPath = Join-Path $portableRoot ($zipBase + ".sha256.txt")

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
    # ZIP libraries omit empty directories: a small marker makes first launch
    # portable even when the shell deliberately carries no model yet.
    Set-Content -LiteralPath (Join-Path $stageDir "PaRuXData\.portable-data") -Value "PaRuX portable storage" -Encoding ASCII
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
rem next to this file. PaRuX.exe also uses this folder directly in the Portable edition.
setlocal
set "PARUX_DATA_ROOT=%~dp0PaRuXData"
if not exist "%PARUX_DATA_ROOT%" mkdir "%PARUX_DATA_ROOT%"
start "" "%~dp0PaRuX.exe"
'@
Set-Content -LiteralPath (Join-Path $stageDir "PaRuX (portable data).cmd") -Value $cmd -Encoding ASCII

$readme = @"
PaRuX $Version - Standard Portable for Windows x64
=================================================
Extract the ZIP on NTFS into a writable folder. Double-click PaRuX.exe.
The `PaRuXData` folder, models, settings, history and knowledge index stay next to the EXE.
This also works when moved to a different drive/USB path. The .cmd is optional.
The EXE and Core include .NET/Windows App SDK; no separate SDK, Python, Ollama,
LM Studio or Windows App Runtime installation is needed.
$(if ($fullyStandalone) { 'INCLUDED: verified CPU llama.cpp, Vulkan llama.cpp, Qwen3-4B local text GGUF, ONNX embedding and reranker models. Basic offline Chat and document search have no model download dependency.' } elseif ($WithoutVulkan) { 'BUILD SHELL: CPU-only base for the Corporate pack builder; not a redistributable standalone application.' } else { 'INCOMPLETE DEVELOPMENT SHELL: no offline Chat weights; do not advertise or release as self-contained.' })
Advanced image/video/training engines are optional and must be installed with PaRuX on demand.
External games/store clients and your Windows graphics/audio drivers are NOT bundled.
Windows SmartScreen, antivirus and IT restrictions still apply. No policy bypass.
To upgrade, replace only application binaries and preserve the PaRuXData directory.
Use the MSI for centrally administered installations, not for a portable USB run.

PaRuX - Standard Portable
==========================
Estrai lo ZIP in una cartella NTFS scrivibile e avvia direttamente PaRuX.exe.
Modelli, Chat, impostazioni, documenti indicizzati e runtime risiedono in PaRuXData
accanto al programma: la cartella puo' essere spostata su un'altra unita'.
Il file .cmd non e' necessario. .NET e Windows App SDK sono inclusi.
$(if ($fullyStandalone) { 'INCLUSI: motori CPU e Vulkan, modello Chat GGUF, embedding e reranker ONNX. Chat e ricerca locale sono utilizzabili senza scaricare modelli aggiuntivi.' } else { 'ATTENZIONE: pacchetto shell per sviluppo; non e completo per la Chat offline.' })
Le funzioni avanzate che richiedono modelli aggiuntivi possono scaricarli su richiesta.
Non sono inclusi driver Windows, giochi e client degli store. Nessuna elusione delle policy IT.
"@
Set-Content -LiteralPath (Join-Path $stageDir "README-portable.txt") -Value $readme -Encoding UTF8

# Full packages get a per-file inventory. The final ZIP verifier re-hashes
# every archive member, not merely the executable. The manifest excludes
# itself; it is generated from strictly staged build assets, never real user data.
if ($fullyStandalone) {
    $fileManifest = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stageDir.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        product = "PaRuX Standard Portable Offline"
        version = $Version
        sourceCommit = $sourceCommit
        modelIncluded = $true
        nativeCpuIncluded = $true
        vulkanIncluded = $true
        embeddingsIncluded = $true
        rerankerIncluded = $true
        runtimeTag = $offline.RuntimeTag
        files = $fileManifest
        fileCount = $fileManifest.Count
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $stageDir "MANIFEST-portable.json") -Encoding UTF8
}

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
    "LICENSE.txt", "README-portable.txt", "PaRuX (portable data).cmd",
    "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
    "PaRuX.Server/hostfxr.dll", "PaRuX.Server/hostpolicy.dll", "PaRuX.Server/coreclr.dll")
if ($IncludeCli) {
    $required += @("Cli/parux.exe", "Cli/hostfxr.dll", "Cli/hostpolicy.dll", "Cli/coreclr.dll")
}
if (-not $WithoutVulkan) { $required += "parux.portable"; $required += "PaRuXData/.portable-data" }
if ($fullyStandalone) {
    $required += "MANIFEST-portable.json"
    $required += "PaRuXData/models/Qwen3-4B-Q4_K_M.gguf"
    $required += "PaRuXData/models/.portable-default-model"
    foreach ($file in @("model.onnx", "vocab.txt", "config.json", "tokenizer_config.json")) {
        $required += "PaRuXData/ai-runtimes/embeddings/" + $file
    }
    foreach ($file in @("model.onnx", "vocab.txt")) {
        $required += "PaRuXData/ai-runtimes/reranker/" + $file
    }
    $required += "PaRuXData/LocalAI/runtime/llama.cpp-" + $offline.RuntimeTag + "-cpu/llama-server.exe"
}
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
    if (@($zip.Entries | Where-Object {
        -not $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or
        $_.FullName.Contains("../") -or $_.FullName.Contains("/./") -or
        $_.FullName.Contains("..\\") -or $_.FullName.Contains("\\")
    }).Count -gt 0) { throw "Portable archive contains an unsafe entry path." }
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Substring($prefix.Length) })
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw "Portable archive contains duplicated entry names." }
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
    $allowedOfflineData = '(?i)^PaRuXData/(?:\.portable-data|models/(?:Qwen3-4B-Q4_K_M\\.gguf|\\.portable-default-model)|ai-runtimes/embeddings/(?:model\\.onnx|vocab\\.txt|config\\.json|tokenizer_config\\.json)|ai-runtimes/reranker/(?:model\\.onnx|vocab\\.txt)|LocalAI/runtime/llama\\.cpp-[^/]+-cpu/[^/]+)$'
    # Even with the full bundle, bootstrap tokens, settings, history, indexes,
    # unknown models and user documents are never legitimate build inputs.
    $bad = @($names | Where-Object {
        ($_ -match $forbiddenPattern -or $_ -match '^PaRuXData/') -and
        $_ -notmatch '(?i)(^|/)(Microsoft\\.|System\\.|onnxruntime|DirectML)' -and
        -not ((-not $WithoutVulkan) -and $_ -ceq "PaRuXData/.portable-data") -and
        -not ($fullyStandalone -and $_ -match $allowedOfflineData)
    })
    if ($bad.Count -gt 0) { throw "Portable ZIP contains unapproved assets, user data or tokens: $($bad | Select-Object -First 5 | Out-String)" }
    if ($fullyStandalone) {
        $manifestEntry = $zip.GetEntry("PaRuX/MANIFEST-portable.json")
        if ($null -eq $manifestEntry) { throw "Portable file inventory missing." }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifestOnDisk = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ([string]$manifestOnDisk.sourceCommit -ne $sourceCommit -or
            [int]$manifestOnDisk.fileCount -ne ($names.Count - 1)) {
            throw "Standalone portable file inventory does not match archive/source."
        }
        $expectedPaths = @($manifestOnDisk.files | ForEach-Object { [string]$_.path })
        if (@($expectedPaths | Sort-Object -Unique).Count -ne $expectedPaths.Count) {
            throw "Portable manifest contains duplicate paths."
        }
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            foreach ($entry in $manifestOnDisk.files) {
                $zipEntry = $zip.GetEntry("PaRuX/" + [string]$entry.path)
                if ($null -eq $zipEntry -or $zipEntry.Length -ne [long]$entry.size) {
                    throw "Portable archive file is missing or has the wrong length: $($entry.path)"
                }
                $stream = $zipEntry.Open()
                try { $shaActual = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace("-", "").ToLowerInvariant() }
                finally { $stream.Dispose() }
                if ($shaActual -ne ([string]$entry.sha256).ToLowerInvariant()) {
                    throw "Portable archive SHA-256 mismatch: $($entry.path)"
                }
            }
        } finally { $hash.Dispose() }
    }
    Write-Host "ZIP check OK: $($names.Count) entries, full file inventory=$(if ($fullyStandalone) { 'verified' } else { 'not applicable to shell' }), no private data."
}
finally { $zip.Dispose() }

if ($Smoke) {
    $smokeDir = Join-Path ([IO.Path]::GetTempPath()) ("parux-portable-smoke-" + [Guid]::NewGuid().ToString("N"))
    $extract = Join-Path $smokeDir "app"
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extract)
    $app = Join-Path $extract "PaRuX"
    $data = if ($WithoutVulkan) { Join-Path $smokeDir "isolated-shell-data" } else { Join-Path $app "PaRuXData" }
    if ($WithoutVulkan) { New-Item -ItemType Directory -Force -Path $data | Out-Null }

    # A portable test that sets PARUX_DATA_ROOT proves nothing about a direct
    # double-click. Clear every process override, then launch the REAL EXE and
    # require the Core bootstrap under the extracted package itself.
    $previousEnv = @{}
    foreach ($name in @("PARUX_DATA_ROOT", "PARUX_LOCALAI_STATE_DIR", "PARUX_CORPORATE", "PARUX_CORPORATE_MODEL_DOWNLOADS")) {
        $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, $null, "Process")
    }
    if ($WithoutVulkan) {
        $env:PARUX_DATA_ROOT = $data
        $env:PARUX_LOCALAI_STATE_DIR = $data
    }
    $proc = $null
    try {
        $proc = Start-Process -FilePath (Join-Path $app "PaRuX.exe") -WorkingDirectory $app -PassThru
        $deadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
        $bootstrap = Join-Path $data "server\bootstrap.json"
        while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $bootstrap)) {
            if ($proc.HasExited) { throw "Portable PaRuX.exe exited early (exit $($proc.ExitCode))." }
            Start-Sleep -Seconds 2
        }
        if (-not (Test-Path -LiteralPath $bootstrap)) {
            throw "Portable Core did not write its bootstrap in the extracted data directory within $SmokeTimeoutSeconds s."
        }
        if (-not $WithoutVulkan) {
            $managedVulkan = Join-Path $data "LocalAI\runtime\llama.cpp-b10549-vulkan\llama-server.exe"
            if (-not (Test-Path -LiteralPath $managedVulkan -PathType Leaf)) {
                $errorPath = Join-Path $data "baseline-bootstrap.error.txt"
                $detail = if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath -Raw } else { "No bootstrap error file." }
                throw "Portable Core failed to stage its local Vulkan baseline: $detail"
            }
            $nativeOut = Join-Path $smokeDir "vulkan-help.stdout.txt"
            $nativeErr = Join-Path $smokeDir "vulkan-help.stderr.txt"
            $native = Start-Process -FilePath $managedVulkan -ArgumentList "--help" -WorkingDirectory (Split-Path -Parent $managedVulkan) -NoNewWindow -PassThru -RedirectStandardOutput $nativeOut -RedirectStandardError $nativeErr
            try {
                if (-not $native.WaitForExit(15000)) {
                    try { $native.Kill() } catch { }
                    throw "Portable Vulkan loader timed out."
                }
                if ($native.ExitCode -ne 0) { throw "Portable Vulkan native DLL loader failed (exit $($native.ExitCode))." }
            } finally { $native.Dispose() }
        }
        if ($fullyStandalone) {
            $cpu = @(Get-ChildItem -LiteralPath (Join-Path $data "LocalAI\runtime") -File -Filter "llama-server.exe" -Recurse |
                Where-Object { $_.DirectoryName -match 'llama\.cpp-[^\\]+-cpu$' })
            if ($cpu.Count -ne 1) { throw "Offline Portable must have one complete CPU runtime in its data directory." }
            $cpuOut = Join-Path $smokeDir "cpu-help.stdout.txt"
            $cpuErr = Join-Path $smokeDir "cpu-help.stderr.txt"
            $cpuProc = Start-Process -FilePath $cpu[0].FullName -ArgumentList "--help" -WorkingDirectory $cpu[0].DirectoryName -NoNewWindow -PassThru -RedirectStandardOutput $cpuOut -RedirectStandardError $cpuErr
            try {
                if (-not $cpuProc.WaitForExit(15000)) {
                    try { $cpuProc.Kill() } catch { }
                    throw "Offline Portable CPU llama.cpp native loader timed out."
                }
                if ($cpuProc.ExitCode -ne 0) { throw "Offline Portable CPU native DLL loader failed (exit $($cpuProc.ExitCode))." }
            } finally { $cpuProc.Dispose() }

            $b = Get-Content -LiteralPath $bootstrap -Raw | ConvertFrom-Json
            $headers = @{ "X-PaRuX-Token" = $b.token }
            $apiRoot = ([string]$b.endpoint).TrimEnd("/")
            $emb = Invoke-RestMethod -Uri ($apiRoot + "/api/v1/embeddings/engine") -Headers $headers -TimeoutSec 120
            $rerank = Invoke-RestMethod -Uri ($apiRoot + "/api/v1/reranker/engine") -Headers $headers -TimeoutSec 120
            if (-not $emb.Installed -or -not $emb.Loaded) {
                throw "Offline Portable embedding ONNX engine did not load from its bundled files."
            }
            if (-not $rerank.Installed -or -not $rerank.Loaded) {
                throw "Offline Portable reranker ONNX engine did not load from its bundled files."
            }
            Write-Host "Smoke OK: double-click EXE isolated; .NET Core started, Vulkan/CPU native loaders and ONNX engines loaded without host dependencies. Real text inference and clean-machine checks still pending."
        } else {
            Write-Host "Shell smoke OK: Core started in isolated location; no claim that offline Chat works."
        }
    }
    finally {
        Get-Process -Name PaRuX, PaRuX.Server -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($extract, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        foreach ($name in $previousEnv.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnv[$name], "Process")
        }
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Done."
