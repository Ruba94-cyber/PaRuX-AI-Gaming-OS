<#
 Shared build-machine asset verification for the complete Standard Portable ZIP.
 This script defines a function only: no network, writes or model installs occur
 until Resolve-PaRuXPortableOfflineAssets is explicitly called by the builder.
 Native dependencies are shipped INSIDE the ZIP, never installed on the target.
#>
function Assert-PaRuXPinnedAsset {
    param([string]$File, [long]$Size, [string]$HashKind, [string]$Digest)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Missing offline asset: $File" }
    $info = Get-Item -LiteralPath $File
    if ($Size -gt 0 -and $info.Length -ne $Size) { throw "Asset size mismatch: $File" }
    if ($HashKind -eq "sha256") {
        $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif ($HashKind -eq "git-sha1") {
        if ($info.Length -gt 1048576) { throw "Unreasonably large Git blob: $File" }
        $bytes = [IO.File]::ReadAllBytes($File)
        $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)" + [char]0)
        $blob = New-Object byte[] ($header.Length + $bytes.Length)
        [Buffer]::BlockCopy($header, 0, $blob, 0, $header.Length)
        [Buffer]::BlockCopy($bytes, 0, $blob, $header.Length, $bytes.Length)
        $hash = [Security.Cryptography.SHA1]::Create()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($blob)).Replace("-", "").ToLowerInvariant() }
        finally { $hash.Dispose() }
    } else { throw "Unsupported asset digest type: $HashKind" }
    if ($actual -ne $Digest.ToLowerInvariant()) { throw "Asset hash mismatch: $File" }
}

function Get-PaRuXPinnedAsset {
    param([string]$Repository, [string]$Revision, [string]$Remote,
          [string]$Destination, [long]$Size, [string]$HashKind, [string]$Digest)
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        try { Assert-PaRuXPinnedAsset $Destination $Size $HashKind $Digest; return }
        catch { Write-Warning "Discarding invalid cached dependency: $Destination" }
    }
    $partial = $Destination + "." + [Guid]::NewGuid().ToString("N") + ".partial"
    try {
        $url = "https://huggingface.co/$Repository/resolve/$Revision/$Remote"
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
        Assert-PaRuXPinnedAsset $partial $Size $HashKind $Digest
        Move-Item -LiteralPath $partial -Destination $Destination -Force -ErrorAction Stop
    } finally { Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue }
}

function Assert-PaRuXNoReparseTree {
    param([string]$PathToInspect)
    if ((Get-Item -LiteralPath $PathToInspect -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Portable asset source cannot be a reparse link: $PathToInspect"
    }
    foreach ($entry in Get-ChildItem -LiteralPath $PathToInspect -Force -Recurse) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Portable asset source has an untrusted symlink/junction: $($entry.FullName)"
        }
    }
}

function Resolve-PaRuXPortableOfflineAssets {
    [CmdletBinding()]
    param(
        [string]$AssetsRoot,
        [string]$ModelPath = "",
        [string]$CpuRuntimeDir = "",
        [string]$EmbeddingDir = "",
        [string]$RerankerDir = "",
        [string]$RuntimeTag = "",
        [switch]$Fetch
    )
    if ([string]::IsNullOrWhiteSpace($AssetsRoot)) { throw "AssetsRoot is required." }
    New-Item -ItemType Directory -Force -Path $AssetsRoot | Out-Null
    if ($Fetch) {
        $releases = Invoke-RestMethod "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20" -Headers @{ "User-Agent" = "PaRuX-build" }
        $release = $releases | Where-Object {
            @($_.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' }).Count -gt 0
        } | Select-Object -First 1
        if ($null -eq $release) { throw "No official llama.cpp CPU x64 runtime release found." }
        $asset = $release.assets | Where-Object { $_.name -match '^llama-.*-bin-win-cpu-x64\.zip$' } | Select-Object -First 1
        $RuntimeTag = [string]$release.tag_name
        if ($RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "Unsafe upstream CPU runtime release tag." }
        $cpuZip = Join-Path $AssetsRoot $asset.name
        $cpuZipPartial = $cpuZip + ".partial"
        $shaField = $asset.PSObject.Properties["digest"]
        $upstreamDigest = if ($null -ne $shaField) { [string]$shaField.Value } else { "" }
        if ($upstreamDigest -notmatch '^sha256:([0-9A-Fa-f]{64})$') {
            throw "The llama.cpp release does not publish SHA-256 metadata; review and pin this CPU asset explicitly before distributing."
        }
        $expectedZipHash = $Matches[1].ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $cpuZip -PathType Leaf) -or
            (Get-FileHash -LiteralPath $cpuZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedZipHash) {
            try {
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $cpuZipPartial -UseBasicParsing -Headers @{ "User-Agent" = "PaRuX-build" }
                Assert-PaRuXPinnedAsset $cpuZipPartial 0 "sha256" $expectedZipHash
                Move-Item -LiteralPath $cpuZipPartial -Destination $cpuZip -Force -ErrorAction Stop
            } finally { Remove-Item -LiteralPath $cpuZipPartial -Force -ErrorAction SilentlyContinue }
        }
        $CpuRuntimeDir = Join-Path $AssetsRoot ("llama-cpu-" + $RuntimeTag)
        if (-not (Test-Path -LiteralPath $CpuRuntimeDir -PathType Container)) {
            Expand-Archive -LiteralPath $cpuZip -DestinationPath $CpuRuntimeDir
        }
        $ModelPath = Join-Path $AssetsRoot "Qwen3-4B-Q4_K_M.gguf"
        Get-PaRuXPinnedAsset "Qwen/Qwen3-4B-GGUF" "main" "Qwen3-4B-Q4_K_M.gguf" $ModelPath 0 "sha256" "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5"
        $EmbeddingDir = Join-Path $AssetsRoot "embeddings"
        $RerankerDir = Join-Path $AssetsRoot "reranker"
        New-Item -ItemType Directory -Force -Path $EmbeddingDir, $RerankerDir | Out-Null
        $embRev = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
        Get-PaRuXPinnedAsset "sentence-transformers/all-MiniLM-L6-v2" $embRev "onnx/model.onnx" (Join-Path $EmbeddingDir "model.onnx") 90405214 "sha256" "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
        Get-PaRuXPinnedAsset "sentence-transformers/all-MiniLM-L6-v2" $embRev "vocab.txt" (Join-Path $EmbeddingDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
        Get-PaRuXPinnedAsset "sentence-transformers/all-MiniLM-L6-v2" $embRev "tokenizer_config.json" (Join-Path $EmbeddingDir "tokenizer_config.json") 350 "git-sha1" "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"
        Get-PaRuXPinnedAsset "sentence-transformers/all-MiniLM-L6-v2" $embRev "config.json" (Join-Path $EmbeddingDir "config.json") 612 "git-sha1" "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
        $rerRev = "a09144355adeed5f58c8ed011d209bf8ee5a1fec"
        Get-PaRuXPinnedAsset "Xenova/ms-marco-MiniLM-L-6-v2" $rerRev "onnx/model.onnx" (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
        Get-PaRuXPinnedAsset "Xenova/ms-marco-MiniLM-L-6-v2" $rerRev "vocab.txt" (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"
    } else {
        if ([string]::IsNullOrWhiteSpace($ModelPath)) { $ModelPath = Join-Path $AssetsRoot "Qwen3-4B-Q4_K_M.gguf" }
        if ([string]::IsNullOrWhiteSpace($EmbeddingDir)) { $EmbeddingDir = Join-Path $AssetsRoot "embeddings" }
        if ([string]::IsNullOrWhiteSpace($RerankerDir)) { $RerankerDir = Join-Path $AssetsRoot "reranker" }
        if ([string]::IsNullOrWhiteSpace($CpuRuntimeDir)) {
            $found = @(Get-ChildItem -LiteralPath $AssetsRoot -Directory -Filter "llama-cpu-*")
            if ($found.Count -ne 1) {
                throw "Supply -CpuRuntimeDir or use -FetchAssets to prepare one verified CPU build. Found: $($found.Count)"
            }
            $CpuRuntimeDir = $found[0].FullName
            if ([string]::IsNullOrWhiteSpace($RuntimeTag)) { $RuntimeTag = $found[0].Name.Substring("llama-cpu-".Length) }
        }
    }

    foreach ($p in @($CpuRuntimeDir, $EmbeddingDir, $RerankerDir)) {
        if (-not (Test-Path -LiteralPath $p -PathType Container)) { throw "Incomplete portable asset set: $p. Use -FetchAssets on the BUILD PC." }
        Assert-PaRuXNoReparseTree $p
    }
    if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) { throw "Portable starter model is absent: $ModelPath" }
    if ((Get-Item -LiteralPath $ModelPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Portable model cannot be a symlink." }
    if ((Get-Item -LiteralPath $ModelPath).Length -lt 65536) { throw "Portable starter GGUF is empty." }
    $modelFileName = [IO.Path]::GetFileName($ModelPath)
    if ($modelFileName -ne "Qwen3-4B-Q4_K_M.gguf") {
        throw "Offline Portable baseline requires the reviewed Qwen3-4B Q4_K_M model; arbitrary model redistribution needs separate license review."
    }
    Assert-PaRuXPinnedAsset $ModelPath 0 "sha256" "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5"

    $embeddingPins = @(
        @("model.onnx",90405214,"sha256","6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
        @("vocab.txt",231508,"git-sha1","fb140275c155a9c7c5a3b3e0e77a9e839594a938"),
        @("config.json",612,"git-sha1","72b987fd805cfa2b58c4c8c952b274a11bfd5a00"),
        @("tokenizer_config.json",350,"git-sha1","c79f2b6a0cea6f4b564fed1938984bace9d30ff0")
    )
    foreach ($pin in $embeddingPins) {
        Assert-PaRuXPinnedAsset (Join-Path $EmbeddingDir $pin[0]) ([long]$pin[1]) $pin[2] $pin[3]
    }
    Assert-PaRuXPinnedAsset (Join-Path $RerankerDir "model.onnx") 90992115 "sha256" "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870"
    Assert-PaRuXPinnedAsset (Join-Path $RerankerDir "vocab.txt") 231508 "git-sha1" "fb140275c155a9c7c5a3b3e0e77a9e839594a938"

    $native = Join-Path $CpuRuntimeDir "llama-server.exe"
    if (-not (Test-Path -LiteralPath $native -PathType Leaf)) {
        $nested = @(Get-ChildItem -LiteralPath $CpuRuntimeDir -Filter "llama-server.exe" -Recurse -File)
        if ($nested.Count -ne 1) { throw "Expected exactly one native CPU llama-server.exe, found $($nested.Count)" }
        $CpuRuntimeDir = $nested[0].DirectoryName
        $native = $nested[0].FullName
    }
    if ((Get-Item -LiteralPath $native).Length -lt 4096) { throw "Native CPU engine executable is unexpectedly small." }
    if ([string]::IsNullOrWhiteSpace($RuntimeTag)) { $RuntimeTag = "bundled" }
    if ($RuntimeTag -notmatch '^[A-Za-z0-9._-]+$') { throw "Unsafe CPU RuntimeTag." }
    return [pscustomobject]@{
        CpuRuntimeDir = $CpuRuntimeDir; RuntimeTag = $RuntimeTag
        ModelPath = $ModelPath; EmbeddingDir = $EmbeddingDir; RerankerDir = $RerankerDir
    }
}
