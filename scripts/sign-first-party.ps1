<#
.SYNOPSIS
    Authenticode-signs and verifies every first-party PE (PaRuX*.exe/.dll and parux.exe) under a directory tree.

.DESCRIPTION
    Third-party and runtime binaries (.NET, WindowsAppSDK, llama.cpp, CUDA, ...) are never touched: only files whose
    name starts with "PaRuX" (or is exactly parux.exe) and that are real PE images are signed. The certificate comes
    from PARUX_SIGN_PFX / PARUX_SIGN_PFX_PASSWORD (read inside this process, never printed). Every file is
    re-verified with "signtool verify /pa". With -VerifyOnly nothing is signed; unsigned first-party PEs fail.
    Files that already carry a signature are skipped so repeated runs are cheap and idempotent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Root,
    [string]$SignTool = "",
    [switch]$VerifyOnly,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Find-SignToolExe {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $pf86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    if (-not [string]::IsNullOrWhiteSpace($pf86)) {
        $kits = Join-Path $pf86 "Windows Kits\10\bin"
        if (Test-Path $kits) {
            $hit = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($hit) { return $hit }
        }
    }
    return $null
}

function Test-PeImage([string]$path) {
    try {
        $stream = [IO.File]::OpenRead($path)
        try {
            if ($stream.Length -lt 64) { return $false }
            $reader = New-Object IO.BinaryReader($stream)
            if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -le 0 -or $peOffset + 4 -gt $stream.Length) { return $false }
            $stream.Position = $peOffset
            return ($reader.ReadUInt32() -eq 0x00004550)
        } finally { $stream.Dispose() }
    } catch { return $false }
}

$Root = [IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Sign root not found: $Root" }
if ([string]::IsNullOrWhiteSpace($SignTool)) { $SignTool = Find-SignToolExe }
if ([string]::IsNullOrWhiteSpace($SignTool)) { throw "signtool.exe was not found. Install the Windows SDK or add signtool to PATH." }

$targets = @(Get-ChildItem -LiteralPath $Root -Recurse -File |
    Where-Object { ($_.Name -like 'PaRuX*' -or $_.Name -ieq 'parux.exe') -and $_.Extension -in @('.exe', '.dll') } |
    Where-Object { Test-PeImage $_.FullName })
if ($targets.Count -eq 0) { throw "No first-party PE files found under $Root." }

$pending = @()
foreach ($file in $targets) {
    $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($null -eq $sig.SignerCertificate) { $pending += $file.FullName }
}

if ($VerifyOnly) {
    if ($pending.Count -gt 0) { throw "Unsigned first-party PE files: $(($pending | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', ')" }
} elseif ($pending.Count -gt 0) {
    $pfx = $env:PARUX_SIGN_PFX
    $pfxPassword = $env:PARUX_SIGN_PFX_PASSWORD
    if ([string]::IsNullOrWhiteSpace($pfx) -or -not (Test-Path -LiteralPath $pfx -PathType Leaf)) { throw "PARUX_SIGN_PFX does not point to a file." }
    if ([string]::IsNullOrWhiteSpace($pfxPassword)) { throw "PARUX_SIGN_PFX_PASSWORD is required." }
    Write-Host "Authenticode signing $($pending.Count) first-party PE file(s) under $Root ..."
    for ($i = 0; $i -lt $pending.Count; $i += 20) {
        $batch = @($pending[$i..([Math]::Min($i + 19, $pending.Count - 1))])
        $output = & $SignTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $pfx /p $pfxPassword @batch 2>&1
        if ($LASTEXITCODE -ne 0) {
            $text = ($output | Out-String).Replace($pfxPassword, '***')
            throw "Authenticode signing failed (exit $LASTEXITCODE): $text"
        }
    }
}

foreach ($file in $targets) {
    $null = & $SignTool verify /pa $file.FullName 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $($file.FullName)." }
}
Write-Host "Authenticode: $($targets.Count) first-party PE file(s) verified under $Root ($($pending.Count) newly signed)."
