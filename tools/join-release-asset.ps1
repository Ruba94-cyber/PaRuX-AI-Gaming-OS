<#
.SYNOPSIS
    Joins <file>.part001, .part002, ... back into <file> and verifies every SHA-256. Plain PowerShell, no third-party tools.

.DESCRIPTION
    Put every part and <file>.parts.sha256.txt in one folder, then run:

        powershell -NoProfile -ExecutionPolicy Bypass -File .\join-release-asset.ps1 -FirstPart .\<file>.part001

    (or pass -Directory and -Name). Steps: each part is hashed and compared to the list (a missing, extra, truncated or
    corrupt part fails before anything is written); the parts are concatenated in order into a temporary file; the
    joined file's SHA-256 must equal the list entry; only then is it renamed to <file>. An existing <file> is not
    overwritten unless -Force. Works on Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding()]
param(
    [string]$FirstPart = "",
    [string]$Directory = "",
    [string]$Name = "",
    [string]$OutputDirectory = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not [string]::IsNullOrWhiteSpace($FirstPart)) {
    $FirstPart = [IO.Path]::GetFullPath($FirstPart)
    $Directory = Split-Path -Parent $FirstPart
    $leaf = [IO.Path]::GetFileName($FirstPart)
    if ($leaf -notmatch '^(?<n>.+)\.part\d{3}$') { throw "-FirstPart must look like <file>.part001." }
    $Name = $Matches['n']
}
if ([string]::IsNullOrWhiteSpace($Directory) -or [string]::IsNullOrWhiteSpace($Name)) { throw "Pass -FirstPart <file>.part001, or both -Directory and -Name." }
$Directory = [IO.Path]::GetFullPath($Directory)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $Directory }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$listPath = Join-Path $Directory "$Name.parts.sha256.txt"
if (-not (Test-Path -LiteralPath $listPath -PathType Leaf)) { throw "Checksum list not found: $listPath" }
$expected = [ordered]@{}
foreach ($line in [IO.File]::ReadAllLines($listPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^(?<h>[0-9a-fA-F]{64})  (?<f>[^\\/]+)$') { throw "Malformed checksum line: $line" }
    $expected[$Matches['f']] = $Matches['h'].ToLowerInvariant()
}
if (-not $expected.Contains($Name)) { throw "Checksum list has no entry for the joined file $Name." }
$wholeExpected = $expected[$Name]
$partNames = @($expected.Keys | Where-Object { $_ -ne $Name })
if ($partNames.Count -lt 2) { throw "Checksum list names fewer than two parts." }
for ($i = 0; $i -lt $partNames.Count; $i++) {
    if ($partNames[$i] -cne ("{0}.part{1:D3}" -f $Name, ($i + 1))) { throw "Checksum list part #$($i + 1) is '$($partNames[$i])'; parts must be numbered consecutively from 001." }
}
$onDisk = @(Get-ChildItem -LiteralPath $Directory -File -Filter "$Name.part*" | ForEach-Object { $_.Name } |
    Where-Object { $_.Substring($Name.Length) -match '^\.part\d+$' })
foreach ($extra in $onDisk) { if ($partNames -cnotcontains $extra) { throw "Unexpected part file not in the checksum list: $extra" } }

$final = Join-Path $OutputDirectory $Name
if ((Test-Path -LiteralPath $final) -and -not $Force) { throw "$final already exists; pass -Force to replace it." }

Write-Host "Verifying $($partNames.Count) part(s) against $([IO.Path]::GetFileName($listPath)) ..."
foreach ($part in $partNames) {
    $partPath = Join-Path $Directory $part
    if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) { throw "Missing part: $part" }
    $actual = (Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $expected[$part]) { throw "SHA-256 mismatch for $part (expected $($expected[$part]), got $actual). Download it again." }
    Write-Host "  OK $part"
}

$temp = "$final.joining"
$buffer = New-Object byte[] (8MB)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $out = [IO.File]::Open($temp, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        foreach ($part in $partNames) {
            $in = [IO.File]::Open((Join-Path $Directory $part), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                while (($read = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $out.Write($buffer, 0, $read)
                    [void]$sha.TransformBlock($buffer, 0, $read, $null, 0)
                }
            } finally { $in.Dispose() }
        }
    } finally { $out.Dispose() }
    [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
    $joined = [BitConverter]::ToString($sha.Hash).Replace('-', '').ToLowerInvariant()
    if ($joined -cne $wholeExpected) { throw "Joined file SHA-256 $joined does not equal the expected $wholeExpected." }
    if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Force }
    Move-Item -LiteralPath $temp -Destination $final
} catch {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    throw
}
Write-Host "Joined: $final"
Write-Host "SHA-256: $joined (matches the published value)"
