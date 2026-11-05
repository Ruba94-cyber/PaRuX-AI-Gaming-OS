<#
.SYNOPSIS
  Runs a command while holding a machine-wide, per-user exclusive "desktop UI" lock (FlaUI / UIA tests).
.DESCRIPTION
  The lock is an OPEN FILE HANDLE (FileShare.None) on %LOCALAPPDATA%\PaRuX\TestCoordination\fla-ui.lock, so ownership
  depends on the live handle, never on the file merely existing; a crashed owner releases it automatically.
  A separate status file (no secrets) records owner/project/PID/time/purpose for the other session to read.
  Before and after acquiring, the script refuses to start if foreign UI test runners (testhost/UITests) are active.
  It never kills processes, never deletes a lock that is held, and never forces a release.
.EXAMPLE
  & tools\Invoke-ExclusiveDesktop.ps1 -Purpose 'PaRuX FlaUI smoke' -Command { dotnet test tests\PaRuX.UITests -c Debug }
#>
param(
    [Parameter(Mandatory)][scriptblock]$Command,
    [string]$Purpose = 'UI test',
    [string]$Project = 'PaRuX',
    [int]$MaxWaitSeconds = 120,
    [int]$PollSeconds = 10
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'PaRuX\TestCoordination'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$lockPath = Join-Path $dir 'fla-ui.lock'
$statusPath = Join-Path $dir 'fla-ui.status.json'

function Get-ForeignUiRunners {
    # Processes that indicate another session is driving the desktop. Our own tree is excluded by PID ancestry.
    $mine = @{}; $cur = $PID
    while ($cur) { $mine[[int]$cur] = $true; $p = Get-CimInstance Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue; $cur = if ($p) { $p.ParentProcessId } else { 0 } }
    Get-CimInstance Win32_Process | Where-Object {
        -not $mine.ContainsKey([int]$_.ProcessId) -and (
            ($_.Name -eq 'testhost.exe' -and $_.CommandLine -match 'UITests|GuiTests|FlaUI') -or
            ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '\btest\b' -and $_.CommandLine -match 'UITests|GuiTests|FlaUI'))
    } | Select-Object ProcessId, Name
}

$deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
$stream = $null
while ($true) {
    $foreign = @(Get-ForeignUiRunners)
    if ($foreign.Count -eq 0) {
        try { $stream = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') } catch [System.IO.IOException] { $stream = $null }
        if ($stream) {
            # Re-check immediately after acquiring.
            $foreign = @(Get-ForeignUiRunners)
            if ($foreign.Count -eq 0) { break }
            $stream.Dispose(); $stream = $null
        }
    }
    if ((Get-Date) -ge $deadline) {
        $owner = if (Test-Path $statusPath) { Get-Content $statusPath -Raw } else { '(no status file)' }
        Write-Error "DESKTOP_BUSY: another session holds or uses the UI-test desktop; NOT started, nothing was killed. Owner: $owner"
        exit 75
    }
    Start-Sleep -Seconds $PollSeconds
}
try {
    @{ owner = $env:USERNAME; project = $Project; pid = $PID; acquiredUtc = (Get-Date).ToUniversalTime().ToString('o'); purpose = $Purpose } |
        ConvertTo-Json | Set-Content -Path $statusPath -Encoding UTF8
    & $Command
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $statusPath -ErrorAction SilentlyContinue
    if ($stream) { $stream.Dispose() }
}
