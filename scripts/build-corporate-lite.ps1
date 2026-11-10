<#
.SYNOPSIS
    Builds PaRuX Corporate Lite Offline Portable (same product, restricted profile) and verifies it.

.DESCRIPTION
    Output: artifacts\corporate-lite\PaRuX-Corporate-Lite-Offline-win-x64.zip (+ SHA-256 and manifest).
    Contents: the app and Core (from build-portable.ps1's stage or a fresh publish), a CPU llama.cpp runtime, ONE small
    quantised model with a redistribution-friendly licence, the light embedding model, offline configuration
    (`parux.corporate` marker, launcher that sets PARUX_CORPORATE=1 and keeps all data in .\PaRuXData), licences and a manifest.
    Never included: documents, indexes, history, tokens, anything from the developer PC. Weights are NOT stored in Git: they are
    supplied with -ModelPath / -EmbeddingDir / -RerankerDir / -RuntimeDir (or fetched with -Fetch from the official sources, hashes recorded).

    The package does not bypass SmartScreen/Defender/IT policy, requires no admin rights from the app and starts no cloud inference.
    Normal launch is offline. A separate IT-approved launcher opts into GGUF-only downloads. Runtime installers, media, Gaming
    and automatic updates stay disabled.

.EXAMPLE
    ./scripts/build-corporate-lite.ps1 -ModelPath D:\assets\model.gguf -EmbeddingDir D:\assets\embeddings -RuntimeDir D:\assets\llama-cpu
    ./scripts/build-corporate-lite.ps1 -Fetch -RequireReranker -Smoke
    ./scripts/build-corporate-lite.ps1 -Fetch -NoModel -RequireReranker
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$ModelPath = "",
    [switch]$NoModel,
    [string]$EmbeddingDir = "",
    [string]$RerankerDir = "",
    [switch]$RequireReranker,
    [string]$RuntimeDir = "",
    [string]$RuntimeTag = "",
    [switch]$Fetch,
    [switch]$SkipPublish,
    [switch]$Smoke,
    [int]$SmokeTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $repoRoot "artifacts\corporate-lite"
$work = Join-Path $outRoot "stage"
$stage = Join-Path $work "PaRuX-Corporate-Lite"
$assets = Join-Path $outRoot "assets"
New-Item -ItemType Directory -Force $outRoot, $assets | Out-Null

# --- 1. The application (reuse the portable build; it already excludes models, indexes and tokens) --------------------------
$portableStage = Join-Path $repoRoot "artifacts\portable\stage\PaRuX"
if (-not $SkipPublish) {
    $pv = @{}; if ($Version) { $pv["Version"] = $Version }
    # Corporate Lite has its own CPU engine and must not inherit the Standard portable Vulkan pack.
    & (Join-Path $PSScriptRoot "build-portable.ps1") @pv -WithoutVulkan
    if ($LASTEXITCODE -ne 0) { throw "Portable build failed." }
}
if (-not (Test-Path (Join-Path $portableStage "PaRuX.exe"))) { throw "Portable stage not found. Run without -SkipPublish." }
if (Test-Path (Join-Path $portableStage "PaRuX.Server\\runtime-baseline")) {
    throw "Corporate Lite requires a CPU-only portable stage. Rebuild without -SkipPublish using -WithoutVulkan."
}
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Path (Join-Path $portableStage "*") -Destination $stage -Recurse -Force
# Not part of this edition: CLI and the portable launchers/readme are replaced below.
Remove-Item (Join-Path $stage "Cli") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stage "PaRuX (portable data).cmd"), (Join-Path $stage "README-portable.txt") -Force -ErrorAction SilentlyContinue
if (-not $Version) {
    [xml]$p = Get-Content (Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj") -Raw
    $Version = [string]($p.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

# --- 2. Optional fetch of official assets (hashes are recorded in the manifest) -----------------------------------------------
function Get-File($url, $dest) {
    if (Test-Path -LiteralPath $dest -PathType Leaf) {
        if ((Get-Item -LiteralPath $dest).Length -le 0) {
            throw "Cached download is empty: $dest. Remove it and rebuild."
        }
        return
    }
    $partial = $dest + "." + [Guid]::NewGuid().ToString("N") + ".partial"
    try {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
        if (-not (Test-Path -LiteralPath $partial -PathType Leaf) -or (Get-Item -LiteralPath $partial).Length -eq 0) {
            throw "Incomplete download: $url"
        }
        Move-Item -LiteralPath $partial -Destination $dest -ErrorAction Stop
    } finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}

# The Core already pins these ONNX models to immutable revisions and checks their
# SHA-256 / Git blob digests. Corporate packaging must honor the SAME bytes so a
# fresh offline user never discovers that the bundled RAG engines are incompatible.
# For Git-backed small files the digest is SHA1("blob " + length + NUL + bytes).
function Assert-PinnedAsset {
    param([string]$File, [long]$Size, [string]$DigestKind, [string]$Digest)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Required pinned asset missing: $File" }
    $item = Get-Item -LiteralPath $File
    if ($item.Length -ne $Size) {
        throw "Pinned asset size mismatch: $File (expected $Size, found $($item.Length))"
    }
    if ($DigestKind -eq "sha256") {
        $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif ($DigestKind -eq "git-sha1") {
        if ($Size -gt 1048576) { throw "Refusing to load a large Git blob into memory: $File" }
        $raw = [IO.File]::ReadAllBytes($File)
        $prefix = [Text.Encoding]::ASCII.GetBytes("blob $($raw.Length)" + [char]0)
        $blob = New-Object byte[] ($prefix.Length + $raw.Length)
        [Buffer]::BlockCopy($prefix, 0, $blob, 0, $prefix.Length)
        [Buffer]::BlockCopy($raw, 0, $blob, $prefix.Length, $raw.Length)
        $sha1 = [Security.Cryptography.SHA1]::Create()
        try { $actual = [BitConverter]::ToString($sha1.ComputeHash($blob)).Replace('-', '').ToLowerInvariant() }
        finally { $sha1.Dispose() }
    } else { throw "Unsupported digest kind: $DigestKind" }
    if ($actual -ne $Digest) { throw "Pinned asset digest mismatch: $File (expected $Digest, got $actual)" }
}
function Get-PinnedAsset {
    param([string]$Repo, [string]$Revision, [string]$RemotePath,
          [string]$Dest, [long]$Size, [string]$DigestKind, [string]$Digest)
    if (Test-Path -LiteralPath $Dest -PathType Leaf) {
        try { Assert-PinnedAsset $Dest $Size $DigestKind $Digest; return }
        catch { Write-Warning "Cached managed model asset is invalid; downloading verified replacement: $Dest" }
    }
    $url = "https://huggingface.co/$Repo/resolve/$Revision/$RemotePath"
    $partial = $Dest + "." + [Guid]::NewGuid().ToString("N") + ".partial"
    try {
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
        Assert-PinnedAsset $partial $Size $DigestKind $Digest
        Move-Item -LiteralPath $partial -Destination $Dest -Force -ErrorAction Stop
    } finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}
if ($Fetch) {
    # llama.cpp (MIT) CPU build from the official releases.
    $rels = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20" -Headers @{ "User-Agent" = "PaRuX-build" }
    $rel = $rels | Where-Object { @($_.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' }).Count -gt 0 } | Select-Object -First 1
    if (-not $rel) { throw "No official win-cpu-x64 llama.cpp asset found in recent releases." }
    $asset = $rel.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' } | Select-Object -First 1
    $RuntimeTag = [string]$rel.tag_name
    if ($RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "Untrusted llama.cpp release tag." }
    $zip = Join-Path $assets $asset.name
    Get-File $asset.browser_download_url $zip
    # GitHub release API SHA-256, when published, is checked before expanding the ZIP.
    # If absent, keep provenance explicit rather than pretending transport TLS is a signed digest.
    if ([string]$asset.digest -match '^sha256:([a-fA-F0-9]{64})$') {
        $runtimeZipSha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($runtimeZipSha -ne $Matches[1].ToLowerInvariant()) {
            throw "CPU llama.cpp release archive digest does not match the upstream release metadata: $zip"
        }
    } else {
        Write-Warning "llama.cpp release metadata has no SHA-256 digest. Archive origin needs manual release review before redistribution."
    }
    # A cached CPU folder must never be relabelled as a newer release.
    $RuntimeDir = Join-Path $assets ("llama-cpu-" + $RuntimeTag)
    if (-not (Test-Path $RuntimeDir)) { Expand-Archive -LiteralPath $zip -DestinationPath $RuntimeDir }
    # Qwen3-4B Q4_K_M, Apache-2.0 (official Qwen repository): clearly better answers than 1.7B for technical questions,
    # still CPU-usable on 16 GB. Q4_K_M is the quality floor PaRuX uses for technical work.
    if (-not $NoModel) {
        $ModelPath = Join-Path $assets "Qwen3-4B-Q4_K_M.gguf"
        Get-File "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf" $ModelPath
    }
    # all-MiniLM-L6-v2 (Apache-2.0): the files the Core's EmbeddingService expects.
    $EmbeddingDir = Join-Path $assets "embeddings"
    New-Item -ItemType Directory -Force $EmbeddingDir | Out-Null
    $embRepo = "sentence-transformers/all-MiniLM-L6-v2"
    $embRev = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
    Get-PinnedAsset $embRepo $embRev "onnx/model.onnx" (Join-Path $EmbeddingDir "model.onnx") 90405214 "sha256" "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
    Get-PinnedAsset $embRepo $embRev "vocab.txt" (Join-Path $EmbeddingDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
    Get-PinnedAsset $embRepo $embRev "tokenizer_config.json" (Join-Path $EmbeddingDir "tokenizer_config.json") 350 "git-sha1" "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"
    Get-PinnedAsset $embRepo $embRev "config.json" (Join-Path $EmbeddingDir "config.json") 612 "git-sha1" "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
    # Cross-encoder for optional local second-stage RAG relevance. The exact
    # model/vocab pair below matches PinnedModelManifest.RerankerMsMarcoMiniLm.
    $RerankerDir = Join-Path $assets "reranker"
    New-Item -ItemType Directory -Force $RerankerDir | Out-Null
    $rerRepo = "Xenova/ms-marco-MiniLM-L-6-v2"
    $rerRev = "a09144355adeed5f58c8ed011d209bf8ee5a1fec"
    Get-PinnedAsset $rerRepo $rerRev "onnx/model.onnx" (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
    Get-PinnedAsset $rerRepo $rerRev "vocab.txt" (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
}
if ($NoModel -and -not [string]::IsNullOrWhiteSpace($ModelPath)) { throw "-NoModel and -ModelPath are mutually exclusive." }
$requiredInputs = @(@("EmbeddingDir", $EmbeddingDir), @("RuntimeDir", $RuntimeDir))
if (-not $NoModel) { $requiredInputs = @(,@("ModelPath", $ModelPath)) + $requiredInputs }
foreach ($pair in $requiredInputs) {
    if ([string]::IsNullOrWhiteSpace($pair[1]) -or -not (Test-Path $pair[1])) { throw "$($pair[0]) is required (or use -Fetch). Not found: '$($pair[1])'" }
}
# Avoid creating a "standalone" archive that silently drops from semantic RAG to lexical-only
# because the model directory existed but the actual files were missing.
foreach ($required in "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json") {
    $file = Join-Path $EmbeddingDir $required
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Corporate Lite requires a complete local embedding engine: $file"
    }
}
# Even if the build uses offline pre-fetched assets, the shipped ONNX weights and
# tokenizer must match the Core's exact pinned baseline. No unreviewed substitutes.
Assert-PinnedAsset (Join-Path $EmbeddingDir "model.onnx") 90405214 "sha256" "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
Assert-PinnedAsset (Join-Path $EmbeddingDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
Assert-PinnedAsset (Join-Path $EmbeddingDir "tokenizer_config.json") 350 "git-sha1" "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"
Assert-PinnedAsset (Join-Path $EmbeddingDir "config.json") 612 "git-sha1" "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
if ($RequireReranker -and [string]::IsNullOrWhiteSpace($RerankerDir)) {
    throw "Complete local RAG requires -RerankerDir or -Fetch. No cloud or later runtime download is permitted."
}
if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
    foreach ($required in "model.onnx", "vocab.txt") {
        $file = Join-Path $RerankerDir $required
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Incomplete local reranker engine: $file"
        }
    }
    Assert-PinnedAsset (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
    Assert-PinnedAsset (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
}
if ($ModelPath -match '(?i)(personal|company|private)') { throw "Refusing a model path that looks private." }
if ($RuntimeTag -and $RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "RuntimeTag contains unsafe characters." }
# Official CPU ZIPs sometimes contain a wrapper folder. Stage the native exe's actual parent.
$native = Join-Path $RuntimeDir "llama-server.exe"
if (-not (Test-Path $native)) {
    $nested = @(Get-ChildItem $RuntimeDir -Filter "llama-server.exe" -Recurse -File)
    if ($nested.Count -ne 1) { throw "CPU runtime must contain exactly one llama-server.exe. Got $($nested.Count)." }
    $RuntimeDir = $nested[0].DirectoryName
    $native = Join-Path $RuntimeDir "llama-server.exe"
}
if (-not (Test-Path $native)) { throw "CPU runtime executable missing." }
if ((Get-Item $native).Length -lt 4096) { throw "CPU runtime exe is unexpectedly small." }

# --- 3. Contained, pre-seeded data root + offline configuration -------------------------------------------------------------
$data = Join-Path $stage "PaRuXData"
$tag = if ($RuntimeTag) { $RuntimeTag } else { "bundled" }
$runtimeDest = Join-Path $data "LocalAI\runtime\llama.cpp-$tag-cpu"
New-Item -ItemType Directory -Force $runtimeDest, (Join-Path $data "models"), (Join-Path $data "ai-runtimes\embeddings") | Out-Null
Copy-Item (Join-Path $RuntimeDir "*") $runtimeDest -Recurse -Force
if (-not $NoModel) { Copy-Item $ModelPath (Join-Path $data "models") -Force }
# Extra models must not silently change the starter fallback.
if (-not $NoModel) { Set-Content (Join-Path (Join-Path $data "models") ".corporate-default-model") ([IO.Path]::GetFileName($ModelPath)) -Encoding UTF8 }
if (-not (Test-Path (Join-Path $runtimeDest "llama-server.exe"))) { throw "Bundled CPU runtime was not staged." }
Copy-Item (Join-Path $EmbeddingDir "*") (Join-Path $data "ai-runtimes\embeddings") -Recurse -Force
if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
    $rerankerDest = Join-Path $data "ai-runtimes\reranker"
    New-Item -ItemType Directory -Force $rerankerDest | Out-Null
    Copy-Item (Join-Path $RerankerDir "*") $rerankerDest -Recurse -Force
}
Set-Content (Join-Path $stage "parux.corporate") "PaRuX Corporate Lite Offline: AI + documents only, no cloud." -Encoding ASCII
$settings = @{ OnboardingCompleted = $true; SetupProfileId = "ai"; StudioModeEnabled = $false; DefaultProfile = "AUTO" } | ConvertTo-Json
Set-Content (Join-Path $data "settings.json") $settings -Encoding UTF8
@'
@echo off
rem Corporate offline default: no external HTTP, data beside app.
setlocal
set "PARUX_CORPORATE=1"
set "PARUX_CORPORATE_MODEL_DOWNLOADS=0"
set "PARUX_DATA_ROOT=%~dp0PaRuXData"
set "PARUX_LOCALAI_STATE_DIR=%~dp0PaRuXData"
start "" "%~dp0PaRuX.exe"
'@ | Set-Content (Join-Path $stage "Start PaRuX Corporate Lite.cmd") -Encoding ASCII
@'
@echo off
rem Use only with IT approval. Allows GGUF model transfers, not cloud inference.
setlocal
set "PARUX_CORPORATE=1"
set "PARUX_CORPORATE_MODEL_DOWNLOADS=1"
set "PARUX_DATA_ROOT=%~dp0PaRuXData"
set "PARUX_LOCALAI_STATE_DIR=%~dp0PaRuXData"
start "" "%~dp0PaRuX.exe"
'@ | Set-Content (Join-Path $stage "Start PaRuX Corporate Model Downloads.cmd") -Encoding ASCII
$modelGuide = if ($NoModel) { "This NoModel edition has NO chat model: import a compatible GGUF before expecting local chat answers." } else { "Local Qwen3-4B GGUF chat model included, no additional download required." }
$ragGuide = if ([string]::IsNullOrWhiteSpace($RerankerDir)) { "Local embeddings included; advanced second-stage reranking is NOT included in this reduced package." } else { "Local embeddings and cross-encoder reranking included for document retrieval." }
@"
PaRuX Corporate Lite Offline $Version
Start with "Start PaRuX Corporate Lite.cmd". No installation and no administrator rights are needed by the application.
CPU runtime and embedding model are included. No runtime setup or Internet needed.
$modelGuide
$ragGuide
Offline: "Start PaRuX Corporate Lite.cmd". Import further GGUF files in Model Hub (files are copied into PaRuXData/models).
For IT-approved GGUF downloads only: "Start PaRuX Corporate Model Downloads.cmd". Model inference remains entirely local;
Gaming, media, online runtime installers, cloud inference and automatic updates stay disabled.
IT network rules still apply; downloads require network permission. Documents remain on this PC.
Licences: see LICENSE.txt and THIRD_PARTY_NOTICES.md (model: Qwen3-4B, Apache-2.0; embeddings: all-MiniLM-L6-v2, Apache-2.0;
llama.cpp: MIT; optional cross-encoder reranker: Xenova/ms-marco-MiniLM-L-6-v2, review source licence before redistribution).

Avvio offline con motore CPU gia' incluso: "Start PaRuX Corporate Lite.cmd".
Importa nuovi GGUF e scegli il modello da Chat. Solo con consenso IT usa "Start PaRuX Corporate Model Downloads.cmd".
"@ | Set-Content (Join-Path $stage "README-corporate-lite.txt") -Encoding UTF8

# --- 4. Manifest with hashes -----------------------------------------------------------------------------------------------
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    product = "PaRuX Corporate Lite Offline"; version = $Version; generatedUtc = [DateTime]::UtcNow.ToString("o")
    modelIncluded = (-not $NoModel)
    model = $(if ($NoModel) { $null } else { [IO.Path]::GetFileName($ModelPath) }); modelSha256 = $(if ($NoModel) { $null } else { (Get-FileHash $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant() })
    runtimeTag = $tag; runtimeIncluded = $true; embeddingIncluded = $true; rerankerIncluded = (-not [string]::IsNullOrWhiteSpace($RerankerDir)); modelImportAllowed = $true; modelDownloadsRequireOptIn = $true
    nativeRuntimeSha256 = (Get-FileHash (Join-Path $runtimeDest "llama-server.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    fileCount = $files.Count; files = $files
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $stage "MANIFEST.json") -Encoding UTF8

# --- 5. ZIP ('/' separators, one top-level folder) -----------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $outRoot "PaRuX-Corporate-Lite-Offline-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$fs = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $stage).Path.TrimEnd('\')
        foreach ($f in Get-ChildItem $root -Recurse -File) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, "PaRuX-Corporate-Lite/" + $f.FullName.Substring($root.Length + 1).Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content (Join-Path $outRoot "PaRuX-Corporate-Lite-Offline-win-x64.sha256.txt") "$sha  PaRuX-Corporate-Lite-Offline-win-x64.zip" -Encoding ASCII
Write-Host "ZIP: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB) SHA-256 $sha"

# --- 6. Verify the finished ZIP ------------------------------------------------------------------------------------------
$z = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($z.Entries | ForEach-Object { $_.FullName })
    if (@($names | Where-Object { $_.Contains('\') }).Count -gt 0) { throw "ZIP has backslash entries." }
    $rel = @($names | ForEach-Object { $_.Substring("PaRuX-Corporate-Lite/".Length) })
    foreach ($req in "PaRuX.exe", "PaRuX.Server/PaRuX.Server.exe", "parux.corporate", "Start PaRuX Corporate Lite.cmd", "MANIFEST.json", "LICENSE.txt",
        "PaRuXData/settings.json", "Start PaRuX Corporate Model Downloads.cmd", "README-corporate-lite.txt") { if ($rel -notcontains $req) { throw "Missing $req" } }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/(parux\.db|history|attachments|server/)' -or $_ -match '(?i)(^|/)(rag\.db|knowledge\.db|bootstrap\.json)$' }).Count -gt 0) { throw "ZIP contains user data/indexes/tokens." }
    $expectedModels = if ($NoModel) { 0 } else { 1 }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/models/.+\.gguf$' }).Count -ne $expectedModels) { throw "Expected $expectedModels bundled model(s)." }
    if (-not $NoModel -and $rel -notcontains "PaRuXData/models/.corporate-default-model") { throw "Bundled default-model marker missing." }
    foreach ($needed in "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json") {
        if ($rel -notcontains "PaRuXData/ai-runtimes/embeddings/$needed") {
            throw "Incomplete standalone embedding engine: $needed"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($RerankerDir)) {
        foreach ($needed in "model.onnx", "vocab.txt") {
            if ($rel -notcontains "PaRuXData/ai-runtimes/reranker/$needed") {
                throw "Incomplete standalone reranker engine: $needed"
            }
        }
    }
    if (@($rel | Where-Object { $_ -match '(?i)^PaRuXData/LocalAI/runtime/llama\.cpp-[^/]+-cpu/llama-server\.exe$' }).Count -ne 1) {
        throw "Bundled CPU runtime executable missing from ZIP."
    }
    $offlineEntry = $z.GetEntry("PaRuX-Corporate-Lite/Start PaRuX Corporate Lite.cmd")
    $onlineEntry = $z.GetEntry("PaRuX-Corporate-Lite/Start PaRuX Corporate Model Downloads.cmd")
    if ($null -eq $offlineEntry -or $null -eq $onlineEntry) { throw "Missing Corporate launchers." }
    $reader = [IO.StreamReader]::new($offlineEntry.Open())
    try { $offlineText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $reader = [IO.StreamReader]::new($onlineEntry.Open())
    try { $onlineText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($offlineText -notmatch 'PARUX_CORPORATE_MODEL_DOWNLOADS=0' -or $onlineText -notmatch 'PARUX_CORPORATE_MODEL_DOWNLOADS=1') {
        throw "Corporate model-download consent was not staged correctly."
    }
    # Verify native CPU engine and starter model bytes from the FINISHED archive, not just the stage.
    $nativePath = @($rel | Where-Object { $_ -match '(?i)^PaRuXData/LocalAI/runtime/llama\.cpp-[^/]+-cpu/llama-server\.exe$' })[0]
    $modelPathInZip = if ($NoModel) { $null } else { @($rel | Where-Object { $_ -match '(?i)^PaRuXData/models/.+\.gguf$' })[0] }
    $shaTool = [Security.Cryptography.SHA256]::Create()
    try {
        $checks = @(@{ entry = $nativePath; expected = $manifest.nativeRuntimeSha256; title = "CPU runtime" })
        if (-not $NoModel) { $checks += @{ entry = $modelPathInZip; expected = $manifest.modelSha256; title = "Starter GGUF" } }
        foreach ($check in $checks) {
            $zipEntry = $z.GetEntry("PaRuX-Corporate-Lite/" + $check.entry)
            if ($null -eq $zipEntry) { throw "Missing $($check.title) archive entry." }
            $stream = $zipEntry.Open()
            try { $actual = [BitConverter]::ToString($shaTool.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($actual -ne $check.expected) { throw "$($check.title) SHA-256 mismatch in finished ZIP." }
        }
    } finally { $shaTool.Dispose() }
    # Verify EVERY staged file from the finished archive, including native DLLs, Core, tokenizer
    # and ONNX assets. The old two-file verification could pass with missing/corrupt embedding DLLs.
    if ($names.Count -ne ($manifest.fileCount + 1)) { throw "Archive inventory count differs from MANIFEST.json." }
    foreach ($item in $files) {
        $entry = $z.GetEntry("PaRuX-Corporate-Lite/" + $item.path)
        if ($null -eq $entry -or $entry.Length -ne $item.size) {
            throw "Archive missing or truncated: $($item.path)"
        }
        $stream = $entry.Open()
        try {
            $shaTool = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($shaTool.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
            finally { $shaTool.Dispose() }
        } finally { $stream.Dispose() }
        if ($actual -ne $item.sha256) { throw "Corporate archive SHA-256 mismatch: $($item.path)" }
    }
    Write-Host "ZIP check OK: $($names.Count) entries, complete file SHA-256 inventory; modelIncluded=$(-not $NoModel); rerankerIncluded=$(-not [string]::IsNullOrWhiteSpace($RerankerDir))."
} finally { $z.Dispose() }

# --- 7. Smoke: extracted package starts offline, Core is up, downloads are refused, document search answers -------------------
if ($Smoke) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("corp-smoke-" + [Guid]::NewGuid().ToString("N"))
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $tmp)
    $app = Join-Path $tmp "PaRuX-Corporate-Lite"
    $env:PARUX_CORPORATE = "1"; $env:PARUX_CORPORATE_MODEL_DOWNLOADS = "0"
    $env:PARUX_DATA_ROOT = Join-Path $app "PaRuXData"; $env:PARUX_LOCALAI_STATE_DIR = $env:PARUX_DATA_ROOT
    # A staged file is not enough: exercise the executable's Windows native DLL loader.
    $nativeExe = @(Get-ChildItem (Join-Path $app "PaRuXData/LocalAI/runtime") -Filter "llama-server.exe" -Recurse -File)
    if ($nativeExe.Count -ne 1) { throw "Offline smoke: expected one bundled CPU llama-server.exe." }
    $stdout = Join-Path $tmp "cpu-help-out.txt"; $stderr = Join-Path $tmp "cpu-help-err.txt"
    $nativeProc = Start-Process $nativeExe[0].FullName -WorkingDirectory $nativeExe[0].DirectoryName -ArgumentList "--help" -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $nativeProc.WaitForExit(15000)) {
        $nativeProc.Kill()
        throw "Offline smoke: native CPU loader timed out."
    }
    if ($nativeProc.ExitCode -ne 0) { throw "Offline smoke: native CPU loader failed (exit $($nativeProc.ExitCode))." }
    $proc = Start-Process (Join-Path $app "PaRuX.exe") -PassThru
    try {
        $boot = Join-Path $env:PARUX_DATA_ROOT "server\bootstrap.json"
        $deadline = (Get-Date).AddSeconds($SmokeTimeoutSeconds)
        while ((Get-Date) -lt $deadline -and -not (Test-Path $boot)) { if ($proc.HasExited) { throw "App exited early." }; Start-Sleep 2 }
        if (-not (Test-Path $boot)) { throw "Core did not start." }
        $b = Get-Content $boot -Raw | ConvertFrom-Json
        $h = @{ "X-PaRuX-Token" = $b.token }
        $r = try { Invoke-WebRequest -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/models/downloads") -Method Post -Headers $h -ContentType "application/json" -Body "{}" -UseBasicParsing } catch { $_.Exception.Response }
        if ([int]$r.StatusCode -ne 403) { throw "Download endpoint was not refused (status $([int]$r.StatusCode))." }
        $s = Invoke-WebRequest -Uri ($b.endpoint.TrimEnd('/') + "/api/v1/knowledge/search?q=test") -Headers $h -UseBasicParsing
        if ($s.StatusCode -ne 200) { throw "Knowledge search not available." }
        Write-Host "Smoke OK: offline start, downloads refused (403), knowledge endpoint reachable. This does NOT verify indexing, source citations or chat inference; run tests/corporate/measure-corporate-lite.ps1 with a bundled model."
    } finally {
        Get-Process PaRuX, PaRuX.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item Env:PARUX_CORPORATE, Env:PARUX_CORPORATE_MODEL_DOWNLOADS, Env:PARUX_DATA_ROOT, Env:PARUX_LOCALAI_STATE_DIR -ErrorAction SilentlyContinue
        Start-Sleep 2; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Done."
