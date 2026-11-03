#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$IncludeUi
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $repo 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj'
$results = Join-Path $repo '.ai/artifacts/graphene-design'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$filter = 'FullyQualifiedName~ThemeResourceRegressionTests|FullyQualifiedName~GrapheneShellLayoutRegressionTests|FullyQualifiedName~GrapheneGamingDesignRegressionTests'
Push-Location $repo
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK was not found. Run this check on a Windows development PC.'
    }

    & dotnet test $testProject -c Debug --filter $filter --logger 'trx;LogFileName=graphene-design.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) {
        throw "Graphene design targeted xUnit regression failed (exit $LASTEXITCODE)."
    }

    if ($IncludeUi) {
        # This UI runner owns its Debug build and test fixtures, unlike a Release launcher.
        & (Join-Path $repo 'tools/test-ui.ps1') -Suite Regression
        if (-not $?) { throw 'Full Debug FlaUI regression did not pass.' }
    }

    Write-Host 'Graphene source regression finished. Real screen/TV contrast and asset review remain mandatory.'
}
finally {
    Pop-Location
}
