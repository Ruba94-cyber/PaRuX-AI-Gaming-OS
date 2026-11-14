#Requires -Version 7
<#
.SYNOPSIS
  Writes a deterministic synthetic Gaming library into an ISOLATED PaRuX data root, for manual and automated performance checks
  of the virtualized library (GAM-004). Synthetic data only: invented names, solid-colour generated PNG covers, no real game
  names, no copyrighted art, executables that do not exist (nothing can be launched).

.DESCRIPTION
  Uses the same storage the app reads:
    <DataRoot>\server\manual-games.json   manual library (version 1)
    <DataRoot>\server\game-titles.json    per-title state: favourite, play time, last played, score, status
    <DataRoot>\game-covers\covers.json    title key -> stored cover name, plus <hash>.png files (content-addressed)
  Names are "Synthetic Game 0001" ... so the title key is "syntheticgame0001" and search "game 19" narrows to 100 titles.
  Re-running with the same arguments produces byte-identical files. Refuses to write outside a directory that is empty or already
  holds only synthetic data from this script, and refuses any path under the real %LOCALAPPDATA%\PaRuX.

.EXAMPLE
  pwsh -File tools\generate-synthetic-game-library.ps1 -Count 2000 -DataRoot C:\Temp\parux-synth-2000
  # then start PaRuX with the environment variable PARUX_DATA_ROOT=C:\Temp\parux-synth-2000 (settings.json is created if missing)
#>
param(
    [int]$Count = 2000,
    [Parameter(Mandatory)][string]$DataRoot,
    # Every Nth title gets a generated cover (1 = all, 0 = none).
    [int]$CoverEvery = 1,
    [int]$CoverWidth = 120,
    [int]$CoverHeight = 180
)
$ErrorActionPreference = 'Stop'
if ($Count -lt 0 -or $Count -gt 10000) { throw 'Count must be between 0 and 10000 (the library file limit).' }

$root = [System.IO.Path]::GetFullPath($DataRoot)
$real = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'PaRuX'))
if ($root.TrimEnd('\') -ieq $real.TrimEnd('\') -or $root.StartsWith($real.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write into the real PaRuX data folder ($real). Use a throw-away directory."
}
$marker = Join-Path $root '.synthetic-game-library'
if ((Test-Path $root) -and (Get-ChildItem -LiteralPath $root -Force | Measure-Object).Count -gt 0 -and -not (Test-Path $marker)) {
    # An isolated UI-test root that only holds settings.json is fine; anything else is not ours.
    $others = Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -notin @('settings.json', 'localai-state') }
    if ($others) { throw "DataRoot '$root' is not empty and was not created by this script." }
}
$server = Join-Path $root 'server'
$covers = Join-Path $root 'game-covers'
New-Item -ItemType Directory -Force -Path $server, $covers | Out-Null
Set-Content -LiteralPath $marker -Value 'synthetic data written by tools/generate-synthetic-game-library.ps1' -Encoding ascii

# ---- minimal solid-colour PNG writer (RGB, 8 bit, zlib) -------------------------------------------------------------------
Add-Type -TypeDefinition @'
public static class SynthCrc
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    public static uint Crc32(byte[] bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
    public static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
'@
function Get-Crc32([byte[]]$bytes) { return [SynthCrc]::Crc32($bytes) }
function Get-BigEndian([uint32]$v) { return , [SynthCrc]::BigEndian($v) }
function New-Chunk([string]$type, [byte[]]$data) {
    $typeBytes = [Text.Encoding]::ASCII.GetBytes($type)
    $body = [byte[]]($typeBytes + $data)
    $ms = [System.IO.MemoryStream]::new()
    $ms.Write((Get-BigEndian ([uint32]$data.Length)), 0, 4)
    $ms.Write($body, 0, $body.Length)
    $ms.Write((Get-BigEndian (Get-Crc32 $body)), 0, 4)
    return , $ms.ToArray()
}
function New-SolidPng([int]$w, [int]$h, [byte]$r, [byte]$g, [byte]$b) {
    $row = [byte[]]::new(1 + 3 * $w)
    for ($x = 0; $x -lt $w; $x++) { $row[1 + 3 * $x] = $r; $row[2 + 3 * $x] = $g; $row[3 + 3 * $x] = $b }
    $raw = [System.IO.MemoryStream]::new()
    for ($y = 0; $y -lt $h; $y++) { $raw.Write($row, 0, $row.Length) }
    $z = [System.IO.MemoryStream]::new()
    $zs = [System.IO.Compression.ZLibStream]::new($z, [System.IO.Compression.CompressionLevel]::SmallestSize, $true)
    $raw.Position = 0; $raw.CopyTo($zs); $zs.Dispose()
    $ihdr = [byte[]]((Get-BigEndian ([uint32]$w)) + (Get-BigEndian ([uint32]$h)) + [byte[]](8, 2, 0, 0, 0))
    $out = [System.IO.MemoryStream]::new()
    $sig = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $out.Write($sig, 0, 8)
    foreach ($chunk in @((New-Chunk 'IHDR' $ihdr), (New-Chunk 'IDAT' $z.ToArray()), (New-Chunk 'IEND' ([byte[]]@())))) { $out.Write($chunk, 0, $chunk.Length) }
    return , $out.ToArray()
}

# ---- fixed 24-colour palette: 24 distinct stored cover files shared by content hash ----------------------------------------
$palette = 0..23 | ForEach-Object {
    $hue = $_ * 15
    $c = ($hue / 60.0); $x = 1 - [math]::Abs(($c % 2) - 1)
    $rgb = switch ([int][math]::Floor($c)) { 0 { @(1, $x, 0) } 1 { @($x, 1, 0) } 2 { @(0, 1, $x) } 3 { @(0, $x, 1) } 4 { @($x, 0, 1) } default { @(1, 0, $x) } }
    , [byte[]]($rgb | ForEach-Object { [byte][math]::Round(60 + 150 * $_) })
}
$stored = @{}
$index = [ordered]@{}
$games = [System.Collections.Generic.List[object]]::new()
$state = [ordered]@{}
$epoch = [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$statuses = @('', 'backlog', 'playing', 'completed', 'onhold', 'dropped')
for ($i = 0; $i -lt $Count; $i++) {
    $name = 'Synthetic Game ' + $i.ToString('D4')
    $key = ($name.ToLowerInvariant() -replace '[^a-z0-9]', '')
    $games.Add([ordered]@{
        Id = 'synth' + $i.ToString('D5'); Name = $name
        ExecutablePath = "C:\synthetic-not-installed\game$i.exe"
        Arguments = ''; WorkingDirectory = ''; Kind = 'game'; Platform = ''; EmulatorId = ''; GameFilePath = ''
    })
    $played = ($i % 5 -eq 0)
    $s = [ordered]@{
        Favorite = ($i % 7 -eq 0); Hidden = $false
        AddedUtc = $epoch.AddMinutes($i).ToString('o')
        Tags = @(); Collections = @()
        PlaySeconds = $(if ($played) { 600 + $i } else { 0 })
        LastPlayedUtc = $(if ($played) { $epoch.AddMinutes($i * 3).ToString('o') } else { $null })
        DefaultInstallationId = $null
        Status = $statuses[$i % $statuses.Count]
        Score = $i % 11
    }
    $state[$key] = $s
    if ($CoverEvery -gt 0 -and $i % $CoverEvery -eq 0) {
        $color = $palette[$i % $palette.Count]
        $fileName = $stored[$i % $palette.Count]
        if (-not $fileName) {
            $png = New-SolidPng $CoverWidth $CoverHeight $color[0] $color[1] $color[2]
            $fileName = ([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($png)).ToLowerInvariant().Substring(0, 32)) + '.png'
            [System.IO.File]::WriteAllBytes((Join-Path $covers $fileName), $png)
            $stored[$i % $palette.Count] = $fileName
        }
        $index[$key] = $fileName
    }
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $server 'manual-games.json'), ([ordered]@{ Version = 1; Games = $games } | ConvertTo-Json -Depth 6), $utf8)
[System.IO.File]::WriteAllText((Join-Path $server 'game-titles.json'), ([ordered]@{ Version = 1; Titles = $state } | ConvertTo-Json -Depth 6), $utf8)
[System.IO.File]::WriteAllText((Join-Path $covers 'covers.json'), ($index | ConvertTo-Json), $utf8)
$settings = Join-Path $root 'settings.json'
if (-not (Test-Path $settings)) {
    [System.IO.File]::WriteAllText($settings, '{"OnboardingCompleted":true,"StudioModeEnabled":false,"BubbleMode":"Disabled"}', $utf8)
}
"Wrote $Count synthetic games, $($index.Count) covers ($($stored.Count) distinct files) to $root"
