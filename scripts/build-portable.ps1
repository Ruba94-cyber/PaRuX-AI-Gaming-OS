<#
.SYNOPSIS
    Builds the PaRuX portable ZIP (same product as the MSI, no separate app) and verifies it.

.DESCRIPTION
    The ZIP contains the essential program files only: PaRuX.exe, the PaRuX.Server Core, the CLI and the licence
    notices. Models, runtimes (llama.cpp, ComfyUI ...), indexes and documents are NOT included: they are optional
    downloads that the person chooses, exactly as with the MSI.

    Where data lives (no fixed paths, no duplication of the storage system):
      * PaRuX.exe                         -> the normal per-user profile, %LOCALAPPDATA%\PaRuX, one profile per PC.
                                             Nothing is carried between computers automatically.
      * "PaRuX (portable data).cmd"       -> sets PARUX_DATA_ROOT to a PaRuXData folder next to the program, so settings,
                                             models, history, RAG folders and indexes stay inside the portable folder.
    Both start the very same PaRuX.exe; the existing PARUX_DATA_ROOT resolver (PaRuXDataRoot) does the rest.

    The ZIP is not signed by this script and does nothing to bypass Windows SmartScreen, Defender or IT policy.
    The MSI-only protected update helper is not part of the portable package: update by downloading the new ZIP.

    -Verify (default) re-opens the finished ZIP, checks the required files, refuses forbidden payload (models, indexes,
    bootstrap tokens) and prints the SHA-256. -Smoke additionally starts the extracted app against a throw-away data
    root and waits for the Core to come up, then stops it.

.EXAMPLE
    ./scripts/build-portable.ps1
    ./scripts/build-portable.ps1 -SkipPublish -Smoke       # reuse artifacts\portable\stage from a previous run
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$Smoke,
    [int]$SmokeTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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
    Write-Host "Publishing PaRuX CLI..."
    & dotnet publish $cliProject -c Release -r win-x64 --self-contained true -o (Join-Path $stageDir "Cli") $common
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)." }
}
if (-not (Test-Path -LiteralPath (Join-Path $stageDir "PaRuX.exe"))) { throw "Nothing staged at $stageDir. Run without -SkipPublish first." }

foreach ($notice in @("LICENSE:LICENSE.txt", "NVIDIA_CUDA_NOTICE.txt:NVIDIA_CUDA_NOTICE.txt", "THIRD_PARTY_NOTICES.md:THIRD_PARTY_NOTICES.md", "OPEN_SOURCE_PROVENANCE.md:OPEN_SOURCE_PROVENANCE.md")) {
    $src, $dst = $notice.Split(':')
    if (Test-Path -LiteralPath (Join-Path $repoRoot $src)) { Copy-Item -LiteralPath (Join-Path $repoRoot $src) -Destination (Join-Path $stageDir $dst) -Force }
}

# The second launcher keeps every piece of user data beside the program. It only sets the documented PARUX_DATA_ROOT.
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
Models, runtimes and indexes are not part of this package. PaRuX offers the downloads you choose on first start.
This package does not bypass Windows SmartScreen, antivirus or IT policy; if your policy blocks unsigned programs, use your
organisation's approved install route. To update, download the new portable ZIP (the MSI-only updater is not included).

PaRuX - pacchetto portable
Estrai in una cartella da cui puoi eseguire programmi e avvia PaRuX.exe (profilo personale di questo PC) oppure
"PaRuX (portable data).cmd" (tutti i dati nella cartella PaRuXData accanto al programma). Modelli, runtime e indici non
sono inclusi. Il pacchetto non aggira SmartScreen, antivirus o policy IT.
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
$required = @("PaRuX.exe", "PaRuX.pri", "PaRuX.Server/PaRuX.Server.exe", "PaRuX.Server/PaRuX.Server.dll", "Cli/parux.exe",
    "LICENSE.txt", "README-portable.txt", "PaRuX (portable data).cmd")
$forbiddenPattern = '(?i)\.(gguf|safetensors|onnx|ckpt|pt|pth|bin\.gz)$|(^|/)rag\.db$|(^|/)bootstrap\.json$|^(models|LocalAI|PaRuXData)/'
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    # The ZIP has one top-level "PaRuX/" folder so extracting never scatters files into the current directory.
    $prefix = "PaRuX/"
    $backslashed = @($zip.Entries | Where-Object { $_.FullName.Contains('\') })
    if ($backslashed.Count -gt 0) { throw "Portable ZIP has $($backslashed.Count) entries with '\' separators (invalid ZIP): $($backslashed[0].FullName)" }
    $names = @($zip.Entries | ForEach-Object { if ($_.FullName.StartsWith($prefix)) { $_.FullName.Substring($prefix.Length) } else { $_.FullName } })
    foreach ($file in $required) { if ($names -notcontains $file) { throw "Portable ZIP is missing required file: $file" } }
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
        Write-Host "Smoke OK: the extracted portable app started its Core with an isolated data root."
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
