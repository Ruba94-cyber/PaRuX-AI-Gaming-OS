<#
.SYNOPSIS
    Splits one large release asset into numbered parts that fit under GitHub's 2 GiB per-file limit.

.DESCRIPTION
    <file> becomes <file>.part001, <file>.part002, ... (each <= -PartSizeBytes, default 1.9 GiB) in -OutputDirectory
    (default: next to the file). A checksum list <file>.parts.sha256.txt is written with the SHA-256 of every part and
    of the joined file, in standard "<hash>  <name>" form. The original file is left untouched and no network
    is used. Re-joining is done by tools/join-release-asset.ps1 (or plain `copy /b`, see docs/guides/PORTABLE.md);
    the join result must match the joined-file hash byte for byte.

.EXAMPLE
    ./tools/split-release-asset.ps1 -Path artifacts\release\4.2.0\PaRuX-4.2.0-portable-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Path,
    [string]$OutputDirectory = "",
    # 1.9 GiB: safely below GitHub's 2 GiB (2147483648 B) release-asset limit.
    [long]$PartSizeBytes = 2040109465,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Path = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Asset not found: $Path" }
if ($PartSizeBytes -lt 1MB -or $PartSizeBytes -ge 2147483648) { throw "PartSizeBytes must be between 1 MiB and 2 GiB - 1 byte." }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Split-Path -Parent $Path }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$name = [IO.Path]::GetFileName($Path)
$total = (Get-Item -LiteralPath $Path).Length
if ($total -le $PartSizeBytes) { throw "$name is $total bytes, which already fits in one part; nothing to split." }
$partCount = [int][Math]::Ceiling($total / [double]$PartSizeBytes)
if ($partCount -gt 999) { throw "Too many parts ($partCount) for the three-digit naming scheme." }

$existing = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter "$name.part*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name.Substring($name.Length) -match '^\.part\d+$' })
$listPath = Join-Path $OutputDirectory "$name.parts.sha256.txt"
if (($existing.Count -gt 0 -or (Test-Path -LiteralPath $listPath)) -and -not $Force) {
    throw "Part files or checksum list for $name already exist in $OutputDirectory; pass -Force to replace them."
}
foreach ($old in $existing) { Remove-Item -LiteralPath $old.FullName -Force }

$buffer = New-Object byte[] (8MB)
$whole = [Security.Cryptography.SHA256]::Create()
$lines = New-Object System.Collections.Generic.List[string]
$source = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    for ($index = 1; $index -le $partCount; $index++) {
        $partName = "{0}.part{1:D3}" -f $name, $index
        $partPath = Join-Path $OutputDirectory $partName
        $partHash = [Security.Cryptography.SHA256]::Create()
        $remaining = [Math]::Min($PartSizeBytes, $total - ($PartSizeBytes * ($index - 1)))
        $target = [IO.File]::Open($partPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            while ($remaining -gt 0) {
                $want = [int][Math]::Min($buffer.Length, $remaining)
                $read = $source.Read($buffer, 0, $want)
                if ($read -le 0) { throw "Unexpected end of $name while writing $partName." }
                $target.Write($buffer, 0, $read)
                [void]$partHash.TransformBlock($buffer, 0, $read, $null, 0)
                [void]$whole.TransformBlock($buffer, 0, $read, $null, 0)
                $remaining -= $read
            }
        } finally { $target.Dispose() }
        [void]$partHash.TransformFinalBlock([byte[]]::new(0), 0, 0)
        $lines.Add(("{0}  {1}" -f ([BitConverter]::ToString($partHash.Hash).Replace('-', '').ToLowerInvariant()), $partName))
        Write-Host ("  wrote {0} ({1:N0} bytes)" -f $partName, (Get-Item -LiteralPath $partPath).Length)
    }
} finally { $source.Dispose() }
[void]$whole.TransformFinalBlock([byte[]]::new(0), 0, 0)
$wholeHash = [BitConverter]::ToString($whole.Hash).Replace('-', '').ToLowerInvariant()

# Cross-check against an independent read of the source so a torn read cannot produce a wrong list.
$independent = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
if ($independent -cne $wholeHash) { throw "Source changed while splitting (hash mismatch); split aborted." }

$lines.Add(("{0}  {1}" -f $wholeHash, $name))
[IO.File]::WriteAllText($listPath, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Split $name ($total bytes) into $partCount part(s); joined SHA-256 $wholeHash"
Write-Host "Checksums: $listPath"
