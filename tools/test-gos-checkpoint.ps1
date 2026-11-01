<#
.SYNOPSIS
  Focused local Windows gate for the PaRuX AI & Gaming OS checkpoint.
.DESCRIPTION
  Builds the current Server and WinUI App, then runs GOS-specific xUnit regression.
  Optional -RunUi launches the real Windows UI acceptance; it requires an
  interactive desktop, proper Windows dependencies and the project's FlaUI setup.
  This script does not install drivers, start games, or publish artifacts.
  All results are from the local working tree. It creates no GitHub Actions.
#>
[CmdletBinding()]
param([switch]$RunUi)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$server = Join-Path $root 'src/PaRuX.Server/PaRuX.Server.csproj'
$app = Join-Path $root 'src/PaRuX.App/PaRuX.App.csproj'
$serverTests = Join-Path $root 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj'
$uiTests = Join-Path $root 'tests/PaRuX.UITests/PaRuX.UITests.csproj'

function Invoke-GosStep {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][string[]]$Arguments)
    Write-Host "[GOS] $Name"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "[GOS] FAIL: $Name (exit code $LASTEXITCODE)"
    }
    Write-Host "[GOS] PASS: $Name"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is missing; install the supported .NET 8 Windows build prerequisites.'
}
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'This GOS checkpoint gate requires Windows (WinUI and optional FlaUI).'
}

Push-Location $root
try {
    Invoke-GosStep 'Server Release build' @('build', $server, '-c', 'Release', '-p:Platform=x64')
    Invoke-GosStep 'WinUI App Release build' @('build', $app, '-c', 'Release', '-p:Platform=x64')
    $testFilter = 'FullyQualifiedName~GamingDisplayTopologyTests|FullyQualifiedName~GamingDisplayTopologySecurityTests|FullyQualifiedName~NativeXInputProbeTests|FullyQualifiedName~GamingProviderInjectionTests|FullyQualifiedName~VoiceGamingTransitionTests|FullyQualifiedName~SpeechProcessAdmissionTests|FullyQualifiedName~SpeechWarmupCoordinatorTests|FullyQualifiedName~ContinuousSpeechCancellationIntegrationTests|FullyQualifiedName~GamingTransitionIntegrationTests|FullyQualifiedName~GamingTransitionConcurrencyTests|FullyQualifiedName~WorkloadOrchestratorTests|FullyQualifiedName~GogGameDiscoveryTests|FullyQualifiedName~GogGameDiscoverySecurityTests|FullyQualifiedName~EpicGameDiscoveryTests|FullyQualifiedName~EpicGameDiscoverySecurityTests|FullyQualifiedName~SteamGameDiscoveryTests|FullyQualifiedName~SteamGameDiscoverySecurityTests|FullyQualifiedName~GamingProfilePersistenceTests|FullyQualifiedName~GamingProfilePersistenceHttpTests|FullyQualifiedName~ManualGameLaunchTests|FullyQualifiedName~ManualGameLaunchSecurityTests|FullyQualifiedName~ManualGameLibraryTests|FullyQualifiedName~ThemeResourceRegressionTests'
    Invoke-GosStep 'GOS focused Server xUnit regression' @('test', $serverTests, '-c', 'Release', '-p:Platform=x64', '--filter', $testFilter)
    if ($RunUi) {
        Invoke-GosStep 'Chat Home FlaUI acceptance' @('test', $uiTests, '-c', 'Release', '-p:Platform=x64', '--filter', 'FullyQualifiedName~ChatHomeStarterTests|FullyQualifiedName~ConsoleModeUiTests|FullyQualifiedName~ManualGameLibraryUiTests')
    } else {
        Write-Host '[GOS] Chat Home FlaUI acceptance: NOT RUN (pass -RunUi on an interactive Windows desktop)'
    }
    Write-Host '[GOS] Physical microphone, owned speech child process, VRAM, controller, display, FPS and streaming: NOT RUN (manual physical acceptance required)'
} finally {
    Pop-Location
}
