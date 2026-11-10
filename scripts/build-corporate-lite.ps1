<#
.SYNOPSIS
    Builds PaRuX Corporate Lite Offline Portable (same product, restricted profile) and verifies it.

.DESCRIPTION
    Output: artifacts\corporate-lite\PaRuX-Corporate-Lite-Offline-win-x64.zip (+ SHA-256 and manifest).
    Contents: the app and Core (from build-portable.ps1's stage or a fresh publish), a CPU llama.cpp runtime, ONE small
    quantised model with a redistribution-friendly licence, the light embedding model, offline configuration
    (`parux.corporate` marker, launcher that sets PARUX_CORPORATE=1 and keeps all data in .\PaRuXData), licences and a manifest.
    Never included: documents, indexes, history, tokens, anything from the developer PC. Weights are NOT stored in Git: they are
    supplied with -ModelPath / -EmbeddingDir / -RuntimeDir (or fetched with -Fetch from the official sources, hashes recorded).

    The package does not bypass SmartScreen/Defender/IT policy, requires no admin rights from the app and starts no cloud inference.
    Normal launch is offline. A separate IT-approved launcher opts into GGUF-only downloads. Runtime installers, media, Gaming
    and automatic updates stay disabled.

.EXAMPLE
    ./scripts/build-corporate-lite.ps1 -ModelPath D:\assets\model.gguf -EmbeddingDir D:\assets\embeddings -RuntimeDir D:\assets\llama-cpu
    ./scripts/build-corporate-lite.ps1 -Fetch -Smoke
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$ModelPath = "",
    [switch]$NoModel,
    [string]$EmbeddingDir = "",
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
    if (Test-Path $dest) { return }
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
}
if ($Fetch) {
    # llama.cpp (MIT) CPU build from the official releases.
    $rels = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20" -Headers @{ "User-Agent" = "PaRuX-build" }
    $rel = $rels | Where-Object { @($_.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' }).Count -gt 0 } | Select-Object -First 1
    if (-not $rel) { throw "No official win-cpu-x64 llama.cpp asset found in recent releases." }
    $asset = $rel.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' } | Select-Object -First 1
    $RuntimeTag = $rel.tag_name
    $zip = Join-Path $assets $asset.name
    Get-File $asset.browser_download_url $zip
    $RuntimeDir = Join-Path $assets "llama-cpu"
    if (-not (Test-Path $RuntimeDir)) { Expand-Archive $zip $RuntimeDir }
    # Qwen3-4B Q4_K_M, Apache-2.0 (official Qwen repository): clearly better answers than 1.7B for technical questions,
    # still CPU-usable on 16 GB. Q4_K_M is the quality floor PaRuX uses for technical work.
    $ModelPath = Join-Path $assets "Qwen3-4B-Q4_K_M.gguf"
    Get-File "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf" $ModelPath
    # all-MiniLM-L6-v2 (Apache-2.0): the files the Core's EmbeddingService expects.
    $EmbeddingDir = Join-Path $assets "embeddings"
    New-Item -ItemType Directory -Force $EmbeddingDir | Out-Null
    $base = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/"
    Get-File ($base + "onnx/model.onnx") (Join-Path $EmbeddingDir "model.onnx")
    foreach ($f in "vocab.txt", "tokenizer_config.json", "config.json") { Get-File ($base + $f) (Join-Path $EmbeddingDir $f) }
}
if ($NoModel -and -not [string]::IsNullOrWhiteSpace($ModelPath)) { throw "-NoModel and -ModelPath are mutually exclusive." }
$requiredInputs = @(@("EmbeddingDir", $EmbeddingDir), @("RuntimeDir", $RuntimeDir))
if (-not $NoModel) { $requiredInputs = @(,@("ModelPath", $ModelPath)) + $requiredInputs }
foreach ($pair in $requiredInputs) {
    if ([string]::IsNullOrWhiteSpace($pair[1]) -or -not (Test-Path $pair[1])) { throw "$($pair[0]) is required (or use -Fetch). Not found: '$($pair[1])'" }
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
@"
PaRuX Corporate Lite Offline $Version
Start with "Start PaRuX Corporate Lite.cmd". No installation and no administrator rights are needed by the application.
CPU runtime and a starter GGUF are already included. No runtime setup or Internet needed.
Offline: "Start PaRuX Corporate Lite.cmd". Import further GGUF files in Model Hub (files are copied into PaRuXData/models).
For IT-approved GGUF downloads only: "Start PaRuX Corporate Model Downloads.cmd". Model inference remains entirely local;
Gaming, media, online runtime installers, cloud inference and automatic updates stay disabled.
IT network rules still apply; downloads require network permission. Documents remain on this PC.
Licences: see LICENSE.txt and THIRD_PARTY_NOTICES.md (model: Qwen3-4B, Apache-2.0; embeddings: all-MiniLM-L6-v2, Apache-2.0;
llama.cpp: MIT).

Avvio offline con motore CPU gia' incluso: "Start PaRuX Corporate Lite.cmd".
Importa nuovi GGUF e scegli il modello da Chat. Solo con consenso IT usa "Start PaRuX Corporate Model Downloads.cmd".
"@ | Set-Content (Join-Path $stage "README-corporate-lite.txt") -Encoding UTF8

# --- 4. Manifest with hashes -----------------------------------------------------------------------------------------------
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); size = $_.Length }
}
$manifest = [ordered]@{
    product = "PaRuX Corporate Lite Offline"; version = $Version; generatedUtc = [DateTime]::UtcNow.ToString("o")
    modelIncluded = (-not $NoModel)
    model = $(if ($NoModel) { $null } else { [IO.Path]::GetFileName($ModelPath) }); modelSha256 = $(if ($NoModel) { $null } else { (Get-FileHash $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant() })
    runtimeTag = $tag; runtimeIncluded = $true; modelImportAllowed = $true; modelDownloadsRequireOptIn = $true
    nativeRuntimeSha256 = (Get-FileHash (Join-Path $runtimeDest "llama-server.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    fileCount = $files.Count
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $stage "MANIFEST.json") -Encoding UTF8

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
    Write-Host "ZIP check OK: $($names.Count) entries; CPU runtime$(if ($NoModel) { ' hash verified; NO model bundled (-NoModel)' } else { ' and starter GGUF hashes verified' })."
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
        Write-Host "Smoke OK: offline start, downloads refused (403), document search answers without any model."
    } finally {
        Get-Process PaRuX, PaRuX.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item Env:PARUX_CORPORATE, Env:PARUX_CORPORATE_MODEL_DOWNLOADS, Env:PARUX_DATA_ROOT, Env:PARUX_LOCALAI_STATE_DIR -ErrorAction SilentlyContinue
        Start-Sleep 2; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "Done."
