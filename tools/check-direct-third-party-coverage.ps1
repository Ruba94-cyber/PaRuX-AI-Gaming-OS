<#
.SYNOPSIS
  Read-only direct NuGet package/name coverage check for THIRD_PARTY_NOTICES.md.
.DESCRIPTION
  Augments (never replaces) tools/validate-license-inventory.ps1 and scripts/generate-sbom.ps1.
  Does not invoke dotnet, download dependencies, generate artifacts or claim legal clearance.
  Use -SelfTest for deterministic synthetic-fixture checks.
#>
[CmdletBinding()]
param([string]$Root = "", [switch]$SelfTest)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-MissingNotices([string[]]$Packages, [string]$NoticeText) {
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($package in ($Packages | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($package)) { continue }
        if ($NoticeText.IndexOf($package, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $missing.Add($package)
        }
    }
    return @($missing.ToArray())
}

if ($SelfTest) {
    $fixture = "| Component | License | Purpose |" + [Environment]::NewLine +
        "| Example.Library | MIT | synthetic |"
    $missing = @(Find-MissingNotices -Packages @("Example.Library", "Missing.Dependency") -NoticeText $fixture)
    if ($missing.Count -ne 1 -or $missing[0] -ne "Missing.Dependency") {
        throw "Synthetic missing-notice test failed."
    }
    $covered = @(Find-MissingNotices -Packages @("Example.Library") -NoticeText $fixture)
    if ($covered.Count -ne 0) { throw "Synthetic positive coverage test failed." }
    Write-Host "[notices] Synthetic fixture tests passed; no repository files changed."
    return
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
$noticePath = Join-Path $Root "THIRD_PARTY_NOTICES.md"
if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
    throw "THIRD_PARTY_NOTICES.md is missing."
}
$notice = Get-Content -LiteralPath $noticePath -Raw
$projects = @(
    "src/PaRuX.App/PaRuX.App.csproj",
    "src/PaRuX.Server/PaRuX.Server.csproj",
    "src/PaRuX.Cli/PaRuX.Cli.csproj",
    "src/PaRuX.WinMlWorker/PaRuX.WinMlWorker.csproj",
    "src/PaRuX.UpdateHelper/PaRuX.UpdateHelper.csproj"
)
$packages = New-Object System.Collections.Generic.List[string]
foreach ($project in $projects) {
    $file = Join-Path $Root $project
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Project missing: $project" }
    [xml]$projectXml = Get-Content -LiteralPath $file -Raw
    foreach ($element in @($projectXml.SelectNodes("//PackageReference[@Include]"))) {
        $package = [string]$element.GetAttribute("Include")
        if (-not [string]::IsNullOrWhiteSpace($package)) { $packages.Add($package) }
    }
}
$missing = @(Find-MissingNotices -Packages $packages.ToArray() -NoticeText $notice)
if ($missing.Count -gt 0) {
    throw "[notices] Missing direct package mentions: $($missing -join ', '). Audit actual licenses and redistribution boundaries before updating notices."
}
Write-Host "[notices] PASS: all $($packages.Count) direct PackageReference entries are mentioned. This is not license clearance or full transitive SBOM validation."
Write-Host "[notices] Run tools/validate-license-inventory.ps1 and scripts/generate-sbom.ps1 on Windows as separate gates."
