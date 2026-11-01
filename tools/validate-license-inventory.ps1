<#
.SYNOPSIS
  Offline license inventory gate for what PaRuX redistributes.
.DESCRIPTION
  1. Every artifact in packaging/windows-baseline-runtime.lock.json has a non-empty license from the allowlist.
  2. Every row of the "Redistributed in the Windows MSI" table in THIRD_PARTY_NOTICES.md has a license cell,
     and no redistributed row declares a strong-copyleft license (GPL/AGPL/SSPL/EUPL/unknown).
  This is an inventory consistency check. It is NOT legal clearance.
#>
[CmdletBinding()]
param([string]$Root)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$errors = New-Object System.Collections.Generic.List[string]

# Licenses accepted for redistributed binaries/source. Anything else needs an explicit owner review.
$allowed = @('MIT', 'Apache-2.0', 'MIT OR Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'Zlib',
             'NVIDIA CUDA Toolkit EULA')
$forbiddenPattern = '(?i)\b(AGPL|GPL|SSPL|EUPL|CPAL|unknown|unlicensed|proprietary-unreviewed)\b'

$lockPath = Join-Path $Root 'packaging/windows-baseline-runtime.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
foreach ($artifact in $lock.artifacts) {
    $id = [string]$artifact.id
    $license = [string]$artifact.license
    if ([string]::IsNullOrWhiteSpace($license)) { $errors.Add("lock artifact '$id' has no license"); continue }
    if ($allowed -notcontains $license) { $errors.Add("lock artifact '$id' uses license '$license' which is not on the reviewed allowlist") }
}

$noticePath = Join-Path $Root 'THIRD_PARTY_NOTICES.md'
$lines = Get-Content -LiteralPath $noticePath
$inTable = $false
$rows = 0
foreach ($line in $lines) {
    if ($line -match '^## Redistributed in the Windows MSI') { $inTable = $true; continue }
    if ($inTable -and $line -match '^## ') { break }
    if (-not $inTable -or $line -notmatch '^\|') { continue }
    if ($line -match '^\|\s*Component\s*\|' -or $line -match '^\|\s*-') { continue }
    $cells = ($line.Trim('|') -split '\|') | ForEach-Object { $_.Trim() }
    if ($cells.Count -lt 3) { $errors.Add("malformed notice row: $line"); continue }
    $rows++
    if ([string]::IsNullOrWhiteSpace($cells[1])) { $errors.Add("notice row '$($cells[0])' has no license") }
    elseif ($cells[1] -match $forbiddenPattern) { $errors.Add("redistributed component '$($cells[0])' declares '$($cells[1])'") }
}
if ($rows -lt 10) { $errors.Add("only $rows notice rows were parsed; the table format may have changed") }

Write-Host "[licenses] $($lock.artifacts.Count) lock artifacts, $rows notice rows checked."
if ($errors.Count -gt 0) {
    foreach ($problem in $errors) { Write-Warning "[licenses] $problem" }
    throw "[licenses] FAIL: $($errors.Count) license inventory problems."
}
Write-Host '[licenses] PASS: inventory consistent (not a legal clearance).'
