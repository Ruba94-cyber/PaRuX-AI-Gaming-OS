<#
.SYNOPSIS
  Windows-only developer validation for native BitTorrent, portable game ZIP
  deployment, and consented temporary AI game config modifications.
.DESCRIPTION
  Local checks only: does not use GitHub Actions, does not download games,
  start real torrent peers, install packages or publish a release.
  Use -Full for the entire Server xUnit suite and the existing solo FlaUI suites.
#>
[CmdletBinding()]
param([switch]$Full)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File exited with code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet is required on this Windows development machine."
    }
    if ($PSVersionTable.PSEdition -eq "Core" -and -not $IsWindows) {
        throw "Run this gate on Windows, not in a Linux cloud session."
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File "tools/check-public-safety.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Public safety guard failed." }

    Invoke-Checked dotnet @("restore", "src/PaRuX.App/PaRuX.App.csproj", "-p:Platform=x64")
    Invoke-Checked dotnet @("build", "src/PaRuX.App/PaRuX.App.csproj", "-p:Platform=x64", "--no-restore")

    $filter = "FullyQualifiedName~EmbeddedMultifileTorrentTests|" +
              "FullyQualifiedName~EmbeddedTorrentTransportTests|" +
              "FullyQualifiedName~NativeTorrentRecoveryStoreTests|" +
              "FullyQualifiedName~GameDownloadTorrentTests|" +
              "FullyQualifiedName~GameDownloadManagerTests|" +
              "FullyQualifiedName~PortableGameInstallServiceTests|" +
              "FullyQualifiedName~TemporaryGameModServiceTests|" +
              "FullyQualifiedName~GameAssetModServiceTests|" +
              "FullyQualifiedName~GameModDropInServiceTests|" +
              "FullyQualifiedName~GameModProcessGuardTests|" +
              "FullyQualifiedName~LibraryAgentToolsTests|" +
              "FullyQualifiedName~BrowserOwnerAndLanHardeningTests"

    Invoke-Checked dotnet @("test", "tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj",
        "-p:Platform=x64", "--no-restore", "--filter", $filter)

    if ($Full) {
        Invoke-Checked dotnet @("test", "tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj",
            "-p:Platform=x64", "--no-restore")
        # FlaUI MUST run sequentially with no other PaRuX app instance.
        & powershell -NoProfile -ExecutionPolicy Bypass -File "tools/test-ui.ps1" -Suite Smoke
        if ($LASTEXITCODE -ne 0) { throw "FlaUI Smoke failed." }
        & powershell -NoProfile -ExecutionPolicy Bypass -File "tools/test-ui.ps1" -Suite Regression
        if ($LASTEXITCODE -ne 0) { throw "FlaUI Regression failed." }
    }
    Write-Host "Source/build checks completed; real peers, game runtime and hardware still need manual acceptance."
}
finally { Pop-Location }
