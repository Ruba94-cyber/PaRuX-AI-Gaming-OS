#Requires -Version 5.1
<#
.SYNOPSIS
  Real-runtime measurement of PERF-003: warm reuse and bounded AUTO escalation between local models.

.DESCRIPTION
  Runs the Core alone against an ISOLATED data root that holds at least two chat models (the larger one may be too big for
  the PC on purpose: the bound must refuse it). For each step it records the llama-server PID and model file actually
  loaded, the time to first streamed token, and the router's own explanation (the model_routed event). Every request is
  bounded by a timeout. Refuses to run against %LOCALAPPDATA%\PaRuX. The result file labels the hardware the Core saw.

  Steps: Ensure x3 (cold start, then two warm no-ops: same PID), AUTO short prompt x2, AUTO long analytical prompt x2,
  AUTO with the "max" intelligence profile (the profile that is allowed to pay for quality) x2.

.EXAMPLE
  powershell -File tools\measure-auto-escalation.ps1 -DataDir C:\PaRuXGos -LayoutDir .\layout\app -OutDir .\out
#>
param(
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$LayoutDir,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$RequestTimeoutSec = 420
)
$ErrorActionPreference = 'Stop'
$real = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'PaRuX'))
if ([IO.Path]::GetFullPath($DataDir).StartsWith($real, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to run against the real PaRuX data folder.' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'result.txt'
function Say([string]$m) { $l = '{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m; Add-Content $log $l; Write-Output $l }
function CoreInfo { $f = Get-ChildItem "$DataDir\root" -Filter bootstrap.json -Recurse -EA 0 | Select-Object -First 1; if ($f) { Get-Content $f.FullName -Raw | ConvertFrom-Json } }
function GApi([string]$path, [string]$method = 'GET', $body = $null, [int]$timeout = 60) {
    $i = CoreInfo; $h = @{ Authorization = "Bearer $($i.token)" }; $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $r = if ($null -ne $body) { Invoke-WebRequest -Uri "$($i.endpoint)$path" -Method $method -Headers $h -Body ($body | ConvertTo-Json -Depth 10) -ContentType 'application/json' -TimeoutSec $timeout -UseBasicParsing }
             else { Invoke-WebRequest -Uri "$($i.endpoint)$path" -Method $method -Headers $h -TimeoutSec $timeout -UseBasicParsing }
        [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $r.Content; Ms = $sw.ElapsedMilliseconds }
    } catch {
        $resp = $_.Exception.Response; $b = ''
        if ($resp) { try { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $b = $sr.ReadToEnd() } catch {} }
        [pscustomobject]@{ Code = $(if ($resp) { [int]$resp.StatusCode } else { 0 }); Body = "$b $($_.Exception.Message)"; Ms = $sw.ElapsedMilliseconds }
    }
}
function LlamaServers { @(Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" | Where-Object { $_.CommandLine -notmatch '--embedding|--reranking' } | ForEach-Object {
    $m = if ($_.CommandLine -match '--model\s+"?([^"]+?\.gguf)') { Split-Path $Matches[1] -Leaf } else { '?' }; [pscustomobject]@{ Pid = [int]$_.ProcessId; Model = $m } }) }
function SetRouting([string]$autoProfile) {
    $r = GApi '/api/v1/chat/model-routing' 'POST' @{ Mode = 'auto'; AutoProfile = $autoProfile } 30
    Say "routing -> auto/$autoProfile : HTTP $($r.Code)"
}
function StartCore {
    Stop-Process -Name PaRuX.Server -Force -EA 0; Start-Sleep 1
    Remove-Item "$DataDir\root\.manual-selection.lock" -EA 0   # a manual lock bypasses AUTO routing entirely
    $env:PARUX_DATA_ROOT = "$DataDir\root"; $env:PARUX_LOCALAI_STATE_DIR = "$DataDir\state"; $env:PARUX_UI_TEST_ISOLATED = '1'
    Remove-Item "$DataDir\root\server\bootstrap.json" -EA 0
    $p = Start-Process "$LayoutDir\PaRuX.Server\PaRuX.Server.exe" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if (CoreInfo) { if ((GApi '/api/v1/health').Code -eq 200) { Say "core pid $($p.Id)"; return } } }
    throw 'Core did not become healthy.'
}
# Streams one chat turn; returns time to first content token, total time, the routed event and the model actually loaded afterwards.
function Chat([string]$prompt, [int]$maxTokens = 32) {
    $i = CoreInfo
    $req = [Net.HttpWebRequest]::Create("$($i.endpoint)/api/v1/chat/completions")
    $req.Method = 'POST'; $req.ContentType = 'application/json'; $req.Headers.Add('Authorization', "Bearer $($i.token)"); $req.Timeout = $RequestTimeoutSec * 1000; $req.ReadWriteTimeout = $RequestTimeoutSec * 1000
    $json = @{ model = 'local-default'; stream = $true; max_tokens = $maxTokens; messages = @(@{ role = 'user'; content = $prompt }) } | ConvertTo-Json -Depth 6
    $bytes = [Text.Encoding]::UTF8.GetBytes($json); $req.ContentLength = $bytes.Length
    $sw = [Diagnostics.Stopwatch]::StartNew(); $routed = $null; $first = $null; $err = $null; $text = ''
    try {
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
        $resp = $req.GetResponse(); $rd = New-Object IO.StreamReader($resp.GetResponseStream())
        while (-not $rd.EndOfStream) {
            $line = $rd.ReadLine()
            if ($line -like 'data: {"parux_event":"model_routed"*') { $routed = ($line.Substring(6) | ConvertFrom-Json) }
            elseif ($line -match '"content":"([^"]+)"' -and $line -notlike '*parux_event*') { if ($null -eq $first) { $first = $sw.ElapsedMilliseconds }; $text += $Matches[1] }
            elseif ($line -like 'data: [[]DONE]') { break }
        }
        $rd.Close(); $resp.Close()
    } catch { $err = $_.Exception.Message
        try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $err += ' ' + $sr.ReadToEnd() } catch {} }
    [pscustomobject]@{ FirstTokenMs = $first; TotalMs = $sw.ElapsedMilliseconds; Routed = $routed; Error = $err; Text = $text; Loaded = (LlamaServers) }
}
function Report([string]$label, $r) {
    $m = ($r.Loaded | ForEach-Object { "$($_.Model)#$($_.Pid)" }) -join ','
    $rt = $r.Routed
    Say ("{0}: ttft={1} ms total={2} ms routedTo={3} mode={4} profile={5} loaded=[{6}] err={7}" -f $label, $r.FirstTokenMs, $r.TotalMs, $rt.selectedModel, $rt.mode, $rt.autoProfile, $m, $(if ($r.Error) { $r.Error.Substring(0, [Math]::Min(160, $r.Error.Length)) } else { '' }))
    if ($rt) { Say ("    reason: " + $rt.reason) }
}

Say "=== PERF-003 measurement; $(Get-Date -Format s)"
StartCore; SetRouting 'balanced'
$h = (GApi '/api/v1/runtime/status').Body | ConvertFrom-Json
Say ("hardware seen by Core: " + (($h.gpuAdapters | Where-Object { -not $_.isSoftware } | ForEach-Object { "$($_.name) ($([math]::Round($_.dedicatedVideoMemoryBytes/1GB,1)) GB dedicated)" }) -join ', ') + "; RAM $([math]::Round($h.ramTotalBytes/1GB,1)) GB; backend $($h.acceleratorProfile.backend)")
Say ("models: " + ((Get-ChildItem "$DataDir\root\models" -Filter *.gguf | Where-Object { $_.Name -notmatch 'Embedding' } | ForEach-Object { "$($_.Name) $([math]::Round($_.Length/1GB,2)) GB" }) -join '; '))

# 1. warm reuse
for ($n = 1; $n -le 3; $n++) {
    $r = GApi '/api/v1/runtime/actions' 'POST' @{ action = 'Ensure' } 300
    $ls = LlamaServers; Say ("Ensure #{0}: HTTP {1} in {2} ms '{3}' llama-server=[{4}]" -f $n, $r.Code, $r.Ms, (($r.Body | ConvertFrom-Json).message), (($ls | ForEach-Object { "$($_.Model)#$($_.Pid)" }) -join ','))
}
$pidWarm = (LlamaServers | Select-Object -First 1).Pid
# 2. AUTO, short prompt (should stay on the loaded model)
foreach ($n in 1, 2) { Report "AUTO(balanced) short #$n" (Chat 'Say hello in five words.' 24) }
# 3. AUTO, long analytical prompt
$long = (1..40 | ForEach-Object { "Observation $_ : the synthetic field station $_ logged rainfall of $($_*3) mm, wind of $($_+7) km/h and a pressure drop of $($_%5+1) hPa during the night shift." }) -join ' '
$longPrompt = "Analyse the following log in depth, compare the stations, explain trends and propose three hypotheses with trade-offs. $long"
foreach ($n in 1, 2) { Report "AUTO(balanced) long analytical #$n" (Chat $longPrompt 48) }
# 4. AUTO with the profile that may pay for quality
SetRouting 'max_intelligence'
foreach ($n in 1, 2) { Report "AUTO(max_intelligence) long analytical #$n" (Chat $longPrompt 48) }
$coder = @(LlamaServers | Where-Object { $_.Model -match 'Coder-30B' })
Say ("bound check: a 30B-class model loaded at any point of the final state: " + ($coder.Count -gt 0))
Stop-Process -Name llama-server, PaRuX.Server -Force -EA 0
Say '=== done'
