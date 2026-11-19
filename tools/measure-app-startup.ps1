<#
.SYNOPSIS
  Repeatable cold-start and idle-cost probe for a desktop app (PaRuX or a competitor) in an ISOLATED profile.
.DESCRIPTION
  Starts the executable, records milliseconds until a top-level window with a title exists (WindowVisibleMs), then samples
  total CPU seconds and working set of the whole process tree for IdleSeconds and stops only the processes it started.
  It never touches a user profile: pass an isolated working directory / environment yourself (portable copy, test data root).
  Output is one JSON object so runs from different apps are comparable.
.EXAMPLE
  .\measure-app-startup.ps1 -Exe C:\x\Playnite.DesktopApp.exe -Label playnite-desktop -Runs 3 -OutFile out.json
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [string]$Label = ([IO.Path]::GetFileNameWithoutExtension($Exe)),
  [string]$Arguments = '',
  [int]$Runs = 3,
  [int]$IdleSeconds = 30,
  [int]$SettleSeconds = 90,
  [int]$TimeoutSeconds = 90,
  [string]$OutFile
)
$ErrorActionPreference = 'Stop'
function Get-Tree([int]$rootId) {
  $all = Get-CimInstance Win32_Process
  $ids = [System.Collections.Generic.HashSet[int]]::new(); [void]$ids.Add($rootId)
  do { $n = $ids.Count; foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId)) { [void]$ids.Add([int]$p.ProcessId) } } } while ($ids.Count -ne $n)
  $ids
}
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $psi = [Diagnostics.ProcessStartInfo]::new($Exe); $psi.Arguments = $Arguments; $psi.WorkingDirectory = [IO.Path]::GetDirectoryName($Exe); $psi.UseShellExecute = $false
  $proc = [Diagnostics.Process]::Start($psi)
  $visible = $null
  while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $tree = Get-Tree $proc.Id
    foreach ($id in $tree) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($p -and $p.MainWindowHandle -ne 0 -and $p.MainWindowTitle) { $visible = $sw.ElapsedMilliseconds; break } }
    if ($visible) { break }
    Start-Sleep -Milliseconds 50
  }
  $cpu0 = 0.0; $samples = @()
  if ($visible) {
    Start-Sleep -Seconds $SettleSeconds   # start-up work (GPU probes, warm-up, plugin loading) must finish before the idle window
    $tree = Get-Tree $proc.Id
    $cpuStart = ($tree | % { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds } | Measure -Sum).Sum
    Start-Sleep -Seconds $IdleSeconds
    $tree = Get-Tree $proc.Id
    $cpuEnd = ($tree | % { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds } | Measure -Sum).Sum
    $ws = ($tree | % { (Get-Process -Id $_ -ErrorAction SilentlyContinue).WorkingSet64 } | Measure -Sum).Sum
    $results += [pscustomobject]@{ Run = $i; WindowVisibleMs = $visible; IdleCpuSecondsOver = [math]::Round($cpuEnd - $cpuStart, 3); IdleWindowSeconds = $IdleSeconds; SettleSeconds = $SettleSeconds; WorkingSetMB = [math]::Round($ws / 1MB, 1); ProcessCount = @($tree).Count }
  } else { $results += [pscustomobject]@{ Run = $i; WindowVisibleMs = $null; Note = 'no window before timeout' } }
  foreach ($id in (Get-Tree $proc.Id)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 3
}
$out = [pscustomobject]@{ Label = $Label; Exe = $Exe; Machine = $env:COMPUTERNAME; Utc = (Get-Date).ToUniversalTime().ToString('o'); Runs = $results }
$json = $out | ConvertTo-Json -Depth 5
if ($OutFile) { $json | Set-Content -Encoding UTF8 $OutFile }
$json
