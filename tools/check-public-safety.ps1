<#
.SYNOPSIS
    Local guard for a PUBLIC repository: fails when a change would publish company or personal data.

.DESCRIPTION
    PaRuX is public, and the document-knowledge features are used on machines that hold confidential
    material. This script inspects what a commit (or the working tree) would ADD and fails on:
      * document / diagnostic / archive / database file types added outside a `Synthetic` folder
        (pdf, Office, dbc, arxml, odx/pdx, cdd, a2l, blf/asc/mf4, pst/eml/msg, zip/7z, db/sqlite, pcap ...);
      * private-key blocks, GitHub / AWS / Slack tokens and JWTs;
      * personal absolute paths (C:\Users\<name>\..., /Users/<name>/..., /home/<name>/...);
      * any term listed in the maintainer-private deny-list (default: tools/public-safety-denylist.local.txt,
        git-ignored, one literal term per line, '#' comments). Put customer / project / ECU / part names there.
    It never prints a matched secret or deny-list term, only the rule, file and line, so its output is safe
    to paste. A line containing `public-safety: allow` is skipped. It only reads; it changes nothing.

    No GitHub Actions: run it locally before committing (or wire it into your own pre-commit hook).

.EXAMPLE
    powershell -File tools\check-public-safety.ps1 -Mode Staged
    powershell -File tools\check-public-safety.ps1                  # working tree vs HEAD, incl. untracked
    powershell -File tools\check-public-safety.ps1 -Mode Range -Range origin/main..HEAD
    powershell -File tools\check-public-safety.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [ValidateSet('Working', 'Staged', 'Range')] [string]$Mode = 'Working',
    [string]$Range = 'origin/main..HEAD',
    [string]$RepoRoot,
    [string]$DenyListPath,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $DenyListPath) { $DenyListPath = Join-Path $PSScriptRoot 'public-safety-denylist.local.txt' }

$blockedExtensions = @(
    'pdf', 'doc', 'docx', 'docm', 'xls', 'xlsx', 'xlsm', 'xlsb', 'ppt', 'pptx', 'rtf', 'odt', 'ods', 'odp',
    'dbc', 'arxml', 'odx', 'pdx', 'cdd', 'a2l', 'blf', 'asc', 'mf4', 'mdf', 'trc',
    'pst', 'ost', 'msg', 'eml',
    'zip', '7z', 'rar', 'tar', 'gz',
    'db', 'sqlite', 'sqlite3', 'mdb', 'accdb', 'bak', 'dump', 'hprof', 'pcap', 'pcapng'
)

# Built from fragments so this file never contains a real-looking secret and does not match itself.
$rules = @(
    @{ Name = 'private-key block';   Regex = ('-----BEGIN ' + '(RSA |EC |DSA |OPENSSH |PGP )?PRIVATE ' + 'KEY') },
    @{ Name = 'GitHub token';        Regex = ('\bgh[pousr]' + '_[A-Za-z0-9]{30,}\b|github' + '_pat_[A-Za-z0-9_]{20,}') },
    @{ Name = 'AWS access key';      Regex = ('\bAK' + 'IA[0-9A-Z]{16}\b') },
    @{ Name = 'Slack token';         Regex = ('\bxox' + '[baprs]-[A-Za-z0-9-]{10,}') },
    @{ Name = 'JWT';                 Regex = ('\bey' + 'J[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}') },
    @{ Name = 'personal Windows path'; Regex = '(?i)\b[A-Z]:\\Users\\(?!Public\b|Default\b|All Users\b|user\b|name\b|you\b|example\b|runner\b|<|%|\$|\{)[A-Za-z0-9._-]{2,}\\' },
    @{ Name = 'personal Unix path';  Regex = '(?i)(?<![A-Za-z0-9])/(Users|home)/(?!public\b|runner\b|user\b|name\b|you\b|example\b|<|\$|\{)[A-Za-z0-9._-]{2,}/' }
)

function Invoke-Git {
    param([string[]]$GitArgs)
    # git writes harmless warnings (e.g. LF/CRLF) to stderr; Windows PowerShell 5 turns those into terminating errors
    # under 'Stop', so run it with 'Continue' and keep only stdout.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $all = @(& git -C $RepoRoot @GitArgs 2>&1) }
    finally { $ErrorActionPreference = $previous }
    $exit = $LASTEXITCODE
    $stdout = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    if ($exit -ne 0) { throw "git $($GitArgs -join ' ') failed with exit code $exit" }
    return $stdout
}

function Test-SyntheticPath([string]$path) {
    return ($path -replace '\\', '/') -match '(?i)(^|/)synthetic(/|$)'
}

function Get-DenyTerms {
    if (-not (Test-Path -LiteralPath $DenyListPath -PathType Leaf)) { return @() }
    return @(Get-Content -LiteralPath $DenyListPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
}

function Invoke-Guard {
    $violations = New-Object System.Collections.Generic.List[string]
    $denyTerms = @(Get-DenyTerms)
    $selfRel = 'tools/check-public-safety.ps1'

    # 1. Which files are being added, and what lines are being added.
    $addedFiles = New-Object System.Collections.Generic.List[string]
    $addedLines = New-Object System.Collections.Generic.List[object]   # @{ Path; Line; Text }

    switch ($Mode) {
        'Staged' { $diffArgs = @('diff', '--cached', '-U0', '--no-color', '--no-renames'); $nameArgs = @('diff', '--cached', '--name-status', '--no-renames') }
        'Range'  { $diffArgs = @('diff', $Range, '-U0', '--no-color', '--no-renames');     $nameArgs = @('diff', $Range, '--name-status', '--no-renames') }
        default  { $diffArgs = @('diff', 'HEAD', '-U0', '--no-color', '--no-renames');     $nameArgs = @('diff', 'HEAD', '--name-status', '--no-renames') }
    }

    foreach ($line in (Invoke-Git $nameArgs)) {
        if ("$line" -match '^A\s+(.+)$') { $addedFiles.Add($Matches[1].Trim()) }
    }
    $untrackedFiles = New-Object System.Collections.Generic.List[string]
    if ($Mode -eq 'Working') {
        foreach ($untracked in (Invoke-Git @('ls-files', '--others', '--exclude-standard'))) {
            $name = "$untracked".Trim()
            if ($name) { $addedFiles.Add($name); $untrackedFiles.Add($name) }
        }
    }

    $currentPath = $null
    $newLine = 0
    foreach ($row in (Invoke-Git $diffArgs)) {
        $text = "$row"
        if ($text -match '^\+\+\+ b/(.+)$') { $currentPath = $Matches[1]; continue }
        if ($text -match '^\+\+\+ /dev/null') { $currentPath = $null; continue }
        if ($text -match '^@@ -\d+(?:,\d+)? \+(\d+)') { $newLine = [int]$Matches[1] - 1; continue }
        if ($text.StartsWith('+') -and -not $text.StartsWith('+++') -and $currentPath) {
            $newLine++
            $addedLines.Add(@{ Path = $currentPath; Line = $newLine; Text = $text.Substring(1) })
        }
    }
    if ($Mode -eq 'Working') {
        foreach ($file in $untrackedFiles) {                         # tracked files are covered by the diff above
            $full = Join-Path $RepoRoot $file
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
            if ((Get-Item -LiteralPath $full).Length -gt 2MB) { continue }
            $n = 0
            foreach ($l in (Get-Content -LiteralPath $full -ErrorAction SilentlyContinue)) {
                $n++
                $addedLines.Add(@{ Path = $file; Line = $n; Text = "$l" })
            }
        }
    }

    # 2. File-type and file-name rules.
    foreach ($file in ($addedFiles | Sort-Object -Unique)) {
        $ext = [IO.Path]::GetExtension($file).TrimStart('.').ToLowerInvariant()
        if ($blockedExtensions -contains $ext -and -not (Test-SyntheticPath $file)) {
            $violations.Add("${file}: document/binary type '.$ext' added outside a Synthetic/ folder")
        }
        for ($i = 0; $i -lt $denyTerms.Count; $i++) {
            if ($file -match [regex]::Escape($denyTerms[$i])) { $violations.Add("${file}: file name matches deny-list term #$($i + 1)") }
        }
    }

    # 3. Content rules on added lines.
    foreach ($entry in $addedLines) {
        $path = $entry.Path
        if (($path -replace '\\', '/') -eq $selfRel) { continue }
        $text = [string]$entry.Text
        if ($text -match 'public-safety:\s*allow') { continue }
        foreach ($rule in $rules) {
            if ($text -match $rule.Regex) { $violations.Add("${path}:$($entry.Line): $($rule.Name)") }
        }
        for ($i = 0; $i -lt $denyTerms.Count; $i++) {
            if ($text -match [regex]::Escape($denyTerms[$i])) { $violations.Add("${path}:$($entry.Line): deny-list term #$($i + 1)") }
        }
    }

    return @($violations | Sort-Object -Unique)
}

function Invoke-SelfTest {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('pubsafe-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        & git -C $tmp init -q 2>&1 | Out-Null
        & git -C $tmp config user.email selftest@example.invalid
        & git -C $tmp config user.name selftest
        Set-Content -LiteralPath (Join-Path $tmp 'base.txt') -Value 'baseline'
        & git -C $tmp add -A; & git -C $tmp commit -q -m base 2>&1 | Out-Null

        $script:RepoRoot = $tmp
        # Outside the temp repo, otherwise the list itself would be scanned as an added file.
        $script:DenyListPath = $tmp + '.deny.txt'
        $script:Mode = 'Working'
        Set-Content -LiteralPath $script:DenyListPath -Value @('# comment', 'Zorblax-Corp')

        $failures = @()
        function Expect([string]$label, [bool]$ok) { if (-not $ok) { $script:selfFailures += $label } }
        $script:selfFailures = @()

        $clean = @(Invoke-Guard)
        Expect 'clean tree passes' ($clean.Count -eq 0)

        New-Item -ItemType Directory -Force (Join-Path $tmp 'tests\Synthetic') | Out-Null
        Set-Content -LiteralPath (Join-Path $tmp 'tests\Synthetic\fake.pdf') -Value 'synthetic'
        Expect 'pdf inside Synthetic passes' (@(Invoke-Guard).Count -eq 0)

        Set-Content -LiteralPath (Join-Path $tmp 'real.dbc') -Value 'x'
        Set-Content -LiteralPath (Join-Path $tmp 'notes.txt') -Value @(
            ('-----BEGIN ' + 'RSA PRIVATE ' + 'KEY-----'),
            ('token = gh' + 'p_' + ('a' * 36)),
            ('see C:\Users\' + 'someone\Documents\plan.docx'),
            'customer Zorblax-Corp ECU list',
            'fine line',
            ('C:\Users\' + 'someone\x  # public-safety: allow'))
        $found = @(Invoke-Guard)
        function Has([string]$regex) { return (@($found | Where-Object { $_ -match $regex }).Count -gt 0) }
        Expect 'dbc outside Synthetic caught' (Has 'real\.dbc')
        Expect 'private key caught' (Has 'notes\.txt:1: private-key')
        Expect 'token caught' (Has 'notes\.txt:2: GitHub token')
        Expect 'personal path caught' (Has 'notes\.txt:3: personal Windows path')
        Expect 'deny-list caught without echoing the term' ((Has 'notes\.txt:4: deny-list term #1') -and -not (Has 'Zorblax'))
        Expect 'allow marker honoured' (-not (Has 'notes\.txt:6'))
        Expect 'clean line not flagged' (-not (Has 'notes\.txt:5'))

        if ($script:selfFailures.Count -gt 0) {
            $script:selfFailures | ForEach-Object { Write-Host "SELFTEST FAIL: $_" }
            return $false
        }
        Write-Host 'SELFTEST PASS (9 checks)'
        return $true
    }
    finally
    {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($tmp + '.deny.txt') -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    if (Invoke-SelfTest) { exit 0 } else { exit 1 }
}

$found = @(Invoke-Guard)
if ($found.Count -eq 0) {
    Write-Host "public-safety: OK ($Mode)"
    exit 0
}
Write-Host "public-safety: $($found.Count) problem(s) - this repository is PUBLIC, do not commit these ($Mode):"
$found | ForEach-Object { Write-Host "  $_" }
Write-Host 'Fix them (use synthetic fixtures under a Synthetic/ folder), or add `public-safety: allow` to a line that is a deliberate false positive.'
exit 1
