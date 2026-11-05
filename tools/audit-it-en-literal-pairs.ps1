<#
.SYNOPSIS
  Source-only spot audit of literal T(en,it) / DT(en,it) view calls.
.DESCRIPTION
  Does not parse arbitrary C# expressions or resource dictionaries; this reports
  only literal pairs. Review dynamic and single-language text separately on Windows.
#>
[CmdletBinding()]
param([string]$Root = "", [switch]$SelfTest)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Inspect-Pairs([string]$Source) {
    # Captures simple C# quoted literal arguments (including C# backslash escapes).
    $pattern = '(?s)\b(?:T|DT)\(\s*"(?<en>(?:\\.|[^"\\])*)"\s*,\s*"(?<it>(?:\\.|[^"\\])*)"'
    $matches = [regex]::Matches($Source, $pattern)
    $empty = @($matches | Where-Object {
        [string]::IsNullOrWhiteSpace($_.Groups['en'].Value) -or
        [string]::IsNullOrWhiteSpace($_.Groups['it'].Value)
    })
    return @{ Total = $matches.Count; Empty = $empty.Count }
}

if ($SelfTest) {
    $good = Inspect-Pairs 'T("Home", "Pagina iniziale")'
    $bad = Inspect-Pairs 'DT("Game", "")'
    if ($good.Total -ne 1 -or $good.Empty -ne 0 -or
        $bad.Total -ne 1 -or $bad.Empty -ne 1) {
        throw "[i18n] Synthetic literal-pair parser self-test failed."
    }
    Write-Host "[i18n] Synthetic fixture tests passed."
    return
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
$views = Join-Path $Root 'src/PaRuX.App/Views'
if (-not (Test-Path -LiteralPath $views -PathType Container)) { throw "Views directory missing." }
$files = @(Get-ChildItem -LiteralPath $views -Filter '*.cs' -Recurse -File)
$total = 0; $empty = 0
foreach ($file in $files) {
    $result = Inspect-Pairs (Get-Content -LiteralPath $file.FullName -Raw)
    $total += $result.Total
    if ($result.Empty -gt 0) {
        Write-Warning "[i18n] $($file.Name): $($result.Empty) empty EN/IT literal pairs"
        $empty += $result.Empty
    }
}
Write-Host "[i18n] Literal pair coverage: $total pairs in $($files.Count) view files; $empty empty pairs."
Write-Host "[i18n] LIMIT: dynamic strings, XAML, accessibility announcements, Server-only messages and translation quality require manual review."
if ($empty -gt 0) { throw "[i18n] Source audit found $empty empty literal pairs." }
