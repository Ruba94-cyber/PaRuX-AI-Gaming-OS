<#
.SYNOPSIS
  Repeatable AI<->Gaming orchestration benchmark (stub providers; no GPU, game or model involved).
.DESCRIPTION
  Runs GamingTransitionBenchmarkTests and prints enter/exit p50/p95/max in milliseconds. Output JSON is
  written to -OutFile (default: bench-results/gaming-transition.json, git-ignored by convention).
  This measures PaRuX orchestration overhead only. FPS, VRAM and real runtime start-up are hardware tests.
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutFile)) { $OutFile = Join-Path $root 'bench-results/gaming-transition.json' }
# The test process runs from its own bin folder, so a relative -OutFile must be made absolute here.
$OutFile = [System.IO.Path]::GetFullPath($OutFile, (Get-Location).Path)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$env:PARUX_BENCH_OUT = $OutFile
Push-Location $root
try {
    dotnet test (Join-Path $root 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj') -c Release `
        --filter 'FullyQualifiedName~GamingTransitionBenchmarkTests' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "[bench] FAIL: benchmark test failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $OutFile)) { throw '[bench] FAIL: no result file was produced' }
    Get-Content -LiteralPath $OutFile -Raw | Write-Host
} finally { Pop-Location; Remove-Item Env:PARUX_BENCH_OUT -ErrorAction SilentlyContinue }
