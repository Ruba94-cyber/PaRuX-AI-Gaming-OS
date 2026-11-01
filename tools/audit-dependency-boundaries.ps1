<#
.SYNOPSIS
  First-party, read-only PaRuX direct dependency and vendored-license gate.
.DESCRIPTION
  Uses only built-in PowerShell/.NET XML and JSON parsing: no Python, npm,
  NuGet restore, network calls, package installation, telemetry or account data.
  Explicitly review and update packaging/dependency-boundaries.json for a
  package addition, removal or version change.
#>
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path -LiteralPath $Root).Path
$baselinePath = Join-Path $rootPath 'packaging/dependency-boundaries.json'
$manifest = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
if ($manifest.schema_version -ne 1) { throw 'Unsupported dependency baseline schema.' }

$errors = [System.Collections.Generic.List[string]]::new()
$referenceCount = 0
$packages = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($project in $manifest.projects.PSObject.Properties) {
    $relative = [string]$project.Name
    if ($relative -notmatch '^src/PaRuX\.(App|Server)/PaRuX\.\1\.csproj$') {
        $errors.Add("Unexpected audited project path: $relative")
        continue
    }
    $projectPath = Join-Path $rootPath ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        $errors.Add("Missing project: $relative")
        continue
    }
    [xml]$xml = Get-Content -LiteralPath $projectPath -Raw
    $actual = @{}
    foreach ($item in $xml.SelectNodes("//*[local-name()='PackageReference']")) {
        $name = [string]$item.GetAttribute('Include')
        $version = [string]$item.GetAttribute('Version')
        if (-not $version) {
            $versionNode = $item.SelectSingleNode("*[local-name()='Version']")
            if ($null -ne $versionNode) { $version = [string]$versionNode.InnerText }
        }
        if ([string]::IsNullOrWhiteSpace($name) -or
            [string]::IsNullOrWhiteSpace($version) -or
            $version.Contains('$(') -or $actual.ContainsKey($name)) {
            $errors.Add("Duplicate, missing or unpinned direct reference in $relative")
            continue
        }
        $actual[$name] = $version
        $referenceCount++
        [void]$packages.Add($name)
    }

    $approved = @{}
    foreach ($p in $project.Value.PSObject.Properties) {
        $approved[$p.Name] = [string]$p.Value
    }
    foreach ($name in $actual.Keys) {
        if (-not $approved.ContainsKey($name)) {
            $errors.Add("New unreviewed dependency in ${relative}: $name")
        } elseif ($approved[$name] -ne $actual[$name]) {
            $errors.Add("Unreviewed version change in ${relative}: $name")
        }
    }
    foreach ($name in $approved.Keys) {
        if (-not $actual.ContainsKey($name)) {
            $errors.Add("Removed reference requires baseline review in ${relative}: $name")
        }
    }
}

foreach ($license in $manifest.vendored_license_files) {
    $relative = [string]$license
    if ([IO.Path]::IsPathRooted($relative) -or
        ($relative -split '[\\/]') -contains '..') {
        $errors.Add('Unsafe vendored-license path in reviewed manifest')
        continue
    }
    if (-not (Test-Path -LiteralPath (Join-Path $rootPath $relative) -PathType Leaf)) {
        $errors.Add("Missing vendored upstream license: $relative")
    }
}

Write-Host "[dependencies] $referenceCount direct references, $($packages.Count) unique packages in reviewed projects."
if ($errors.Count -gt 0) {
    foreach ($problem in $errors) { Write-Warning "[dependencies] $problem" }
    throw "[dependencies] FAIL: $($errors.Count) dependency boundary deviations."
}
Write-Host '[dependencies] PASS: no unreviewed direct dependency changes; upstream license files present.'
