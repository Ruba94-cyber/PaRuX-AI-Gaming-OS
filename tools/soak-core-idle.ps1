# PERF-002 idle soak for PaRuX Core (Windows PowerShell 5.1 compatible).
#
# Launches a published Core in a throw-away isolated data root (PARUX_DATA_ROOT, PARUX_LOCALAI_STATE_DIR), leaves it idle
# (no client, no model, no request), and samples Core plus every child process at a fixed interval:
#   private bytes, working set, handles, threads, and CPU% (share of all logical CPUs over the last interval).
# Every sample is written to a CSV. The run FAILS (non-zero exit) when
#   - private memory (Core + children) grew by more than -MaxPrivateGrowthMB between the early baseline and the late window, or
#   - CPU stayed above -MaxCpuPercent for -CpuSustainSamples consecutive samples (a runaway, not a start-up burst).
# It never touches the real PaRuX data, always stops what it started, and bounds every wait.
#
# Usage (do not run while another benchmark owns the machine):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\soak-core-idle.ps1 -DurationMinutes 120 -OutCsv .ai\artifacts\perf-002\soak.csv
#
# Exit codes: 0 pass, 1 memory growth, 2 sustained CPU, 3 both, 4 harness or startup failure.
param(
    [string]$ServerPath = '',
    [int]$DurationMinutes = 120,
    [int]$SampleSeconds = 30,
    [string]$OutCsv = '',
    [int]$StartupTimeoutSeconds = 90,
    [int]$WarmupSamples = 6,
    [int]$WindowSamples = 6,
    [double]$MaxPrivateGrowthMB = 300,
    [double]$MaxCpuPercent = 5,
    [int]$CpuSustainSamples = 10,
    [switch]$KeepData,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# The CSV is data, not display text: every number is formatted with the invariant culture so a comma-decimal locale
# (for example Italian) never puts commas inside a comma-separated row. The header is fixed.
$script:CsvHeader = 'timestamp,elapsedSeconds,processes,privateMB,workingSetMB,handles,threads,cpuPercent'
function Format-SoakCsvRow($Row) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    return [string]::Format($inv, '{0},{1},{2},{3},{4},{5},{6},{7}', $Row.Timestamp, $Row.Elapsed, $Row.Processes,
        $Row.PrivateMB, $Row.WorkingSetMB, $Row.Handles, $Row.Threads, $Row.Cpu)
}
if ($SelfTest) {
    # Formats a row under a comma-decimal culture and requires exactly eight fields with dot decimals.
    $previous = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('it-IT')
        $row = [pscustomobject]@{ Timestamp = '2026-01-01T00:00:00.0000000Z'; Elapsed = 30; Processes = 3; PrivateMB = 123.4
            WorkingSetMB = 98.5; Handles = 1200; Threads = 40; Cpu = 0.125 }
        $line = Format-SoakCsvRow $row
        $fields = $line.Split(',')
        if ($fields.Count -ne 8) { Write-Host ('SELFTEST FAIL: ' + $fields.Count + ' fields in: ' + $line); exit 1 }
        if ($fields[3] -ne '123.4' -or $fields[4] -ne '98.5' -or $fields[7] -ne '0.125') { Write-Host ('SELFTEST FAIL: ' + $line); exit 1 }
        if ($script:CsvHeader.Split(',').Count -ne 8) { Write-Host 'SELFTEST FAIL: header'; exit 1 }
    } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $previous }
    Write-Host 'SELFTEST OK'
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $ServerPath) { $ServerPath = Join-Path $root 'src\PaRuX.Server\bin\Release\net10.0\PaRuX.Server.exe' }
if ($SampleSeconds -lt 5) { $SampleSeconds = 5 }
if ($DurationMinutes -lt 1) { $DurationMinutes = 1 }
if (-not $OutCsv) { $OutCsv = Join-Path ([IO.Path]::GetTempPath()) ('parux-soak-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv') }
$cpuCount = [Environment]::ProcessorCount

function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    return [double]$sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
}

# Core plus all descendants, from the live process table (CIM has a built-in operation timeout via the session).
function Get-ProcessTreeIds([int]$rootId) {
    $ids = New-Object 'System.Collections.Generic.List[int]'
    $ids.Add($rootId)
    $all = @(Get-CimInstance -ClassName Win32_Process -OperationTimeoutSec 20 -Property ProcessId, ParentProcessId)
    $queue = New-Object 'System.Collections.Generic.Queue[int]'
    $queue.Enqueue($rootId)
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        foreach ($child in $all) {
            if ([int]$child.ParentProcessId -eq $parent -and -not $ids.Contains([int]$child.ProcessId)) {
                $ids.Add([int]$child.ProcessId)
                $queue.Enqueue([int]$child.ProcessId)
            }
        }
    }
    return , $ids.ToArray()
}

function Read-Tree([int]$rootId) {
    $privateBytes = 0.0; $workingSet = 0.0; $handles = 0; $threads = 0; $cpuSeconds = 0.0; $count = 0
    foreach ($id in (Get-ProcessTreeIds $rootId)) {
        try {
            $p = [Diagnostics.Process]::GetProcessById($id)
            $privateBytes += $p.PrivateMemorySize64
            $workingSet += $p.WorkingSet64
            $handles += $p.HandleCount
            $threads += $p.Threads.Count
            $cpuSeconds += $p.TotalProcessorTime.TotalSeconds
            $count++
            $p.Dispose()
        } catch { }   # a child can exit between listing and reading; it simply drops out of this sample
    }
    return New-Object psobject -Property @{
        PrivateMB = [math]::Round($privateBytes / 1MB, 1); WorkingSetMB = [math]::Round($workingSet / 1MB, 1)
        Handles = $handles; Threads = $threads; CpuSeconds = $cpuSeconds; Processes = $count
    }
}

function Wait-CoreHealth([string]$dataRoot, [int]$timeoutSeconds) {
    $bootstrap = Join-Path (Join-Path $dataRoot 'server') 'bootstrap.json'
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $endpoint = ([string](Get-Content -LiteralPath $bootstrap -Raw -ErrorAction Stop | ConvertFrom-Json).endpoint).TrimEnd('/')
            $request = [Net.WebRequest]::Create($endpoint + '/api/v1/health')
            $request.Timeout = 2000
            $response = $request.GetResponse(); $code = [int]$response.StatusCode; $response.Close()
            if ($code -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Stop-Tree([int]$rootId) {
    try { & taskkill.exe /PID $rootId /T /F 2>&1 | Out-Null } catch { }
}

if (-not (Test-Path -LiteralPath $ServerPath)) {
    Write-Host ('Core executable not found: ' + $ServerPath)
    exit 4
}

$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('parux-soak-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $dataRoot, (Join-Path $dataRoot 'state') -Force | Out-Null
$outDir = Split-Path -Parent $OutCsv
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$core = $null
$rows = New-Object System.Collections.ArrayList
$exitCode = 0
try {
    $psi = New-Object Diagnostics.ProcessStartInfo $ServerPath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = Split-Path -Parent $ServerPath
    $psi.EnvironmentVariables['PARUX_DATA_ROOT'] = $dataRoot
    $psi.EnvironmentVariables['PARUX_LOCALAI_STATE_DIR'] = (Join-Path $dataRoot 'state')
    $core = [Diagnostics.Process]::Start($psi)
    Write-Host ('Core started, PID ' + $core.Id + ', data root ' + $dataRoot)

    if (-not (Wait-CoreHealth $dataRoot $StartupTimeoutSeconds)) {
        Write-Host ('Core did not answer /api/v1/health within ' + $StartupTimeoutSeconds + ' s.')
        $exitCode = 4
    } else {
        $total = [int][math]::Ceiling($DurationMinutes * 60 / $SampleSeconds)
        $startedAt = Get-Date
        $previous = Read-Tree $core.Id
        $previousAt = Get-Date
        $sustained = 0; $maxSustained = 0
        Add-Content -LiteralPath $OutCsv -Value $script:CsvHeader -Encoding ASCII
        for ($i = 1; $i -le $total; $i++) {
            Start-Sleep -Seconds $SampleSeconds
            if ($core.HasExited) { Write-Host 'Core exited during the soak.'; $exitCode = 4; break }
            $now = Get-Date
            $sample = Read-Tree $core.Id
            $seconds = ($now - $previousAt).TotalSeconds
            $cpuPercent = 0.0
            if ($seconds -gt 0) { $cpuPercent = [math]::Round((($sample.CpuSeconds - $previous.CpuSeconds) / $seconds) * 100 / $cpuCount, 3) }
            if ($cpuPercent -lt 0) { $cpuPercent = 0.0 }   # a child exited, so the summed CPU time dropped
            $previous = $sample; $previousAt = $now
            $row = New-Object psobject -Property @{
                Timestamp = $now.ToString('o'); Elapsed = [int]($now - $startedAt).TotalSeconds; Processes = $sample.Processes
                PrivateMB = $sample.PrivateMB; WorkingSetMB = $sample.WorkingSetMB; Handles = $sample.Handles
                Threads = $sample.Threads; Cpu = $cpuPercent
            }
            [void]$rows.Add($row)
            Add-Content -LiteralPath $OutCsv -Encoding ASCII -Value (Format-SoakCsvRow $row)
            if ($cpuPercent -gt $MaxCpuPercent) { $sustained++ } else { $sustained = 0 }
            if ($sustained -gt $maxSustained) { $maxSustained = $sustained }
        }

        if ($exitCode -eq 0) {
            $failures = @()
            $privateSeries = @($rows | ForEach-Object { [double]$_.PrivateMB })
            if ($privateSeries.Count -ge ($WarmupSamples + $WindowSamples)) {
                # Baseline: the window right after warm-up (start-up allocation settled). Late: the final window.
                $baseline = Get-Median ($privateSeries[$WarmupSamples..($WarmupSamples + $WindowSamples - 1)])
                $late = Get-Median ($privateSeries[($privateSeries.Count - $WindowSamples)..($privateSeries.Count - 1)])
                $growth = [math]::Round($late - $baseline, 1)
                Write-Host ('Private memory: baseline median ' + $baseline + ' MB, late median ' + $late + ' MB, growth ' + $growth + ' MB (limit ' + $MaxPrivateGrowthMB + ' MB).')
                if ($growth -gt $MaxPrivateGrowthMB) { $failures += 'memory'; $exitCode = $exitCode -bor 1 }
            } else {
                Write-Host ('Too few samples (' + $privateSeries.Count + ') to judge memory growth; need at least ' + ($WarmupSamples + $WindowSamples) + '.')
            }
            Write-Host ('Longest run above ' + $MaxCpuPercent + '% CPU: ' + $maxSustained + ' samples (limit ' + $CpuSustainSamples + ').')
            if ($maxSustained -ge $CpuSustainSamples) { $failures += 'cpu'; $exitCode = $exitCode -bor 2 }
            if ($failures.Count -eq 0) { Write-Host 'PASS: no memory growth or sustained CPU beyond the thresholds.' }
            else { Write-Host ('FAIL: ' + ($failures -join ', ')) }
        }
    }
} catch {
    Write-Host ('Soak harness failure: ' + $_.Exception.Message)
    $exitCode = 4
} finally {
    if ($core -ne $null) {
        try { if (-not $core.HasExited) { Stop-Tree $core.Id } } catch { }
        try { [void]$core.WaitForExit(15000) } catch { }
    }
    Start-Sleep -Milliseconds 800
    if (-not $KeepData) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ('CSV: ' + $OutCsv)
}
exit $exitCode
