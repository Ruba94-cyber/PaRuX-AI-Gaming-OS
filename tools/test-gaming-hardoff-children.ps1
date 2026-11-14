#Requires -Version 5.1
<#
.SYNOPSIS
  Real-process proof of the Gaming hard-off (GOS-09) for the managed AI children that are not the text runtime:
  speech worker, retrieval (llama-server --embedding), uv install child, LoRA conversion child.

.DESCRIPTION
  For each child and each round: start the REAL child through the product endpoints, wait until it is alive, launch a REAL
  non-elevated game from the library (Core: POST /api/v1/gaming/library/{id}/launch), then measure how long the child takes
  to disappear, assert that starting the same AI work is refused while Gaming is active, end the game, return from Gaming
  and assert that nothing was started again on its own.

  Runs the Core alone (PaRuX.Server.exe from a published layout) against an ISOLATED data root. Never touches a real profile:
  the data directory must not be under %LOCALAPPDATA%\PaRuX. Every wait is bounded; every failure is reported, never skipped.
  Setup is idempotent and uses product endpoints only (speech engine install, retrieval model download, LoRA engine install).
  The result file records the GPU adapters the Core saw, so the hardware is always labelled.

.EXAMPLE
  powershell -File tools\test-gaming-hardoff-children.ps1 -DataDir C:\PaRuXGos -LayoutDir .\layout\app -OutDir .\out -Rounds 3
#>
param(
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$LayoutDir,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Rounds = 3,
    [string]$GameId = '',
    [string[]]$Children = @('speech', 'retrieval', 'uv', 'lora'),
    [int]$ChildAliveTimeoutSec = 40,
    [int]$ExitBoundMs = 5000
)
$ErrorActionPreference = 'Stop'
$real = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'PaRuX'))
if ([IO.Path]::GetFullPath($DataDir).StartsWith($real, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to run against the real PaRuX data folder.' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'result.txt'
function Say([string]$m) { $l = '{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m; Add-Content $log $l; Write-Output $l }

function CoreInfo {
    $f = Get-ChildItem "$DataDir\root" -Filter bootstrap.json -Recurse -EA 0 | Select-Object -First 1
    if ($f) { Get-Content $f.FullName -Raw | ConvertFrom-Json }
}
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
function CorePid { (Get-Process PaRuX.Server -EA 0 | Select-Object -First 1).Id }
function Descendants([int]$root) {
    $all = Get-CimInstance Win32_Process; $set = @{ $root = $true }; $out = @(); $changed = $true
    while ($changed) { $changed = $false; foreach ($p in $all) { if ($set.ContainsKey([int]$p.ParentProcessId) -and -not $set.ContainsKey([int]$p.ProcessId)) { $set[[int]$p.ProcessId] = $true; $changed = $true; $out += $p } } }
    $out
}
function ChildProcs([string]$kind) {
    foreach ($p in (Descendants (CorePid))) {
        $c = [string]$p.CommandLine; $n = [string]$p.Name
        $hit = switch ($kind) {
            'speech'    { $n -eq 'python.exe' -and ($c -match 'ai-runtimes.speech') }
            'retrieval' { $n -eq 'llama-server.exe' -and $c -match '--embedding' }
            'uv'        { $n -eq 'uv.exe' }
            'lora'      { $n -eq 'python.exe' -and $c -match 'convert_lora_to_gguf|lora-conversion' }
        }
        if ($hit) { $p }
    }
}
function StartCore {
    Stop-Process -Name PaRuX.Server -Force -EA 0; Start-Sleep 1
    $env:PARUX_DATA_ROOT = "$DataDir\root"; $env:PARUX_LOCALAI_STATE_DIR = "$DataDir\state"; $env:PARUX_UI_TEST_ISOLATED = '1'
    Remove-Item "$DataDir\root\server\bootstrap.json" -EA 0
    $p = Start-Process "$LayoutDir\PaRuX.Server\PaRuX.Server.exe" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if (CoreInfo) { $h = GApi '/api/v1/health'; if ($h.Code -eq 200) { Say "core pid $($p.Id) $((CoreInfo).endpoint)"; return } } }
    throw 'Core did not become healthy.'
}

# ---------- setup through product endpoints (idempotent) ----------
function SetupSpeech {
    $s = (GApi '/api/v1/speech/engine').Body | ConvertFrom-Json
    if (-not $s.ttsInstalled) { $r = GApi '/api/v1/speech/engine/install' 'POST' @{} 600; Say "setup speech install: HTTP $($r.Code) in $($r.Ms) ms"; if ($r.Code -ne 200) { throw 'speech install failed' } }
}
function SetupRetrieval {
    $f = "$DataDir\root\models\Qwen3-Embedding-0.6B-Q8_0.gguf"
    if (-not (Test-Path $f)) {
        $ev = @{ metadataStatus = 'declared'; publisherDeclaredLicense = 'apache-2.0'; metadataObservedUtc = (Get-Date).ToUniversalTime().ToString('o'); reviewRequired = $false; reviewAcknowledged = $false }
        $r = GApi '/api/v1/models/download-plan/start' 'POST' @{ repoId = 'Qwen/Qwen3-Embedding-0.6B-GGUF'; quantization = 'Q8_0'; setName = 'Qwen3-Embedding-0.6B-Q8_0'; revision = '370f27d7550e0def9b39c1f16d3fbaa13aa67728'; licenseEvidence = $ev } 60
        Say "setup retrieval model download (Apache-2.0, 639 MB, revision pinned): HTTP $($r.Code)"
        for ($i = 0; $i -lt 120 -and -not (Test-Path $f); $i++) { Start-Sleep 5 }
        if (-not (Test-Path $f)) { throw 'retrieval model download did not finish' }
    }
    $e = (GApi '/api/v1/embeddings/engine').Body | ConvertFrom-Json
    if (-not $e.installed) { $r = GApi '/api/v1/embeddings/engine/install' 'POST' @{} 300; Say "setup MiniLM baseline: HTTP $($r.Code)" }
    $docs = "$DataDir\docs"
    if (-not (Test-Path $docs)) {
        New-Item -ItemType Directory -Force $docs | Out-Null
        1..24 | ForEach-Object { $n = $_; $t = "# Synthetic note $n`n"; 1..6 | ForEach-Object { $t += "Section $_ of note $n describes the fictional Aurora field station $n in the valley $_. The station records rainfall at hour $($n*$_), stores readings in ledger $($n+$_) and reports to the visitor centre every $($_+1) days. Volunteers number $($n+10) and the weather mast stands $($_*3+$n) metres tall.`n`n" }; Set-Content "$docs\note-$n.md" $t }
        $r = GApi '/api/v1/rag/sources' 'POST' @{ folderPath = $docs }; $id = ($r.Body | ConvertFrom-Json).id
        $r = GApi "/api/v1/rag/sources/$id/reindex" 'POST' @{} 600; Say "setup retrieval corpus: HTTP $($r.Code) $($r.Body)"
    }
}
function RaceBody {
    $docs = ((GApi '/api/v1/rag/performance/evaluation-documents').Body | ConvertFrom-Json).documents
    $cases = @(); foreach ($n in 1..12) { $doc = $docs | Where-Object { $_.fileName -eq "note-$n.md" }; $rel = @{}; $rel[$doc.documentId] = 2; $cases += @{ id = "c$n"; query = "How tall is the weather mast at Aurora field station $n and who reports readings from note $n"; relevanceByDocumentId = $rel } }
    @{ repoId = 'Qwen/Qwen3-Embedding-0.6B-GGUF'; fileName = 'Qwen3-Embedding-0.6B-Q8_0.gguf'; modelId = 'qwen3-embedding-0.6b'; dimensions = 1024; revision = '370f27d7550e0def9b39c1f16d3fbaa13aa67728'; cases = $cases }
}
$script:AdapterId = $null; $script:BaseDir = $null
function SetupLora {
    $st = (GApi '/api/v1/adapters/conversion/status').Body | ConvertFrom-Json
    if (-not $st.dependenciesVerified) { $r = GApi '/api/v1/adapters/conversion/install' 'POST' @{} 600; Say "setup LoRA engine install: HTTP $($r.Code) in $($r.Ms) ms"; if ($r.Code -ne 200) { throw 'lora engine install failed' } }
    $ad = "$DataDir\lora-synth\adapter"; $base = "$DataDir\lora-synth\base"
    if (-not (Test-Path "$ad\adapter_model.safetensors")) {
        New-Item -ItemType Directory -Force $ad, $base | Out-Null
        $py = "$DataDir\root\ai-runtimes\lora-conversion\.venv\Scripts\python.exe"
        $code = @'
import json,sys,torch
from safetensors.torch import save_file
ad,base=sys.argv[1],sys.argv[2]
h,L,inter,heads,vocab,r=64,2,128,4,128,4
json.dump({"architectures":["LlamaForCausalLM"],"model_type":"llama","hidden_size":h,"num_hidden_layers":L,"intermediate_size":inter,"num_attention_heads":heads,"num_key_value_heads":heads,"vocab_size":vocab,"max_position_embeddings":256,"rms_norm_eps":1e-5,"rope_theta":10000.0,"hidden_act":"silu","tie_word_embeddings":False,"torch_dtype":"float32"},open(base+"/config.json","w"))
json.dump({"peft_type":"LORA","task_type":"CAUSAL_LM","r":r,"lora_alpha":8,"target_modules":["q_proj","v_proj"],"base_model_name_or_path":"synthetic/tiny-llama","bias":"none","lora_dropout":0.0,"fan_in_fan_out":False},open(ad+"/adapter_config.json","w"))
t={}
g=torch.Generator().manual_seed(7)
for l in range(L):
    for m in ("q_proj","v_proj"):
        p=f"base_model.model.model.layers.{l}.self_attn.{m}"
        t[p+".lora_A.weight"]=torch.randn(r,h,generator=g)*0.02
        t[p+".lora_B.weight"]=torch.randn(h,r,generator=g)*0.02
save_file(t,ad+"/adapter_model.safetensors")
'@
        Set-Content "$DataDir\lora-synth\make.py" $code
        & $py -I "$DataDir\lora-synth\make.py" $ad $base | Out-Null
    }
    $existing = @((GApi '/api/v1/adapters').Body | ConvertFrom-Json)
    $hit = $existing | Where-Object { $_.name -eq 'synthetic-tiny-lora' } | Select-Object -First 1
    if (-not $hit) { $r = GApi '/api/v1/adapters' 'POST' @{ name = 'synthetic-tiny-lora'; baseModel = 'synthetic/tiny-llama'; adapterPath = $ad; method = 'LoRA'; notes = 'synthetic fixture' }; $hit = $r.Body | ConvertFrom-Json }
    $script:AdapterId = $hit.id; $script:BaseDir = $base
}

# ---------- child start / refusal probes ----------
$jobBlock = {
    param($ep, $tok, $path, $json, $timeout)
    $h = @{ Authorization = "Bearer $tok" }
    try { $r = Invoke-WebRequest -Uri "$ep$path" -Method POST -Headers $h -Body $json -ContentType 'application/json' -TimeoutSec $timeout -UseBasicParsing; "$([int]$r.StatusCode)" }
    catch { $x = $_.Exception.Response; "$(if ($x) { [int]$x.StatusCode } else { 0 }) $($_.Exception.Message)" }
}
function BeginWork([string]$kind) {
    $i = CoreInfo
    switch ($kind) {
        'retrieval' { Start-Job $jobBlock -ArgumentList $i.endpoint, $i.token, '/api/v1/rag/performance/provider-race/embedding', (RaceBody | ConvertTo-Json -Depth 10), 300 }
        'uv'        { Remove-Item "$DataDir\root\ai-runtimes\cutout-studio" -Recurse -Force -EA 0
                      Start-Job $jobBlock -ArgumentList $i.endpoint, $i.token, '/api/v1/cutout/engine/install', '{}', 300 }
        'lora'      { Remove-Item "$DataDir\lora-synth\adapter\adapter.gguf" -EA 0
                      Start-Job $jobBlock -ArgumentList $i.endpoint, $i.token, "/api/v1/adapters/$($script:AdapterId)/convert", (@{ outtype = 'f16'; baseModelConfigDir = $script:BaseDir } | ConvertTo-Json), 300 }
        'speech'    { Start-Job $jobBlock -ArgumentList $i.endpoint, $i.token, '/api/v1/speech/synthesize', (@{ text = 'Local models keep your notes private.'; voice = '' } | ConvertTo-Json), 300 }
    }
}
function ProbeRefusal([string]$kind) {
    switch ($kind) {
        'speech'    { GApi '/api/v1/speech/synthesize' 'POST' @{ text = 'blocked'; voice = '' } 30 }
        'retrieval' { GApi '/api/v1/rag/performance/provider-race/embedding' 'POST' (RaceBody) 60 }
        'uv'        { GApi '/api/v1/cutout/engine/install' 'POST' @{} 60 }
        'lora'      { GApi "/api/v1/adapters/$($script:AdapterId)/convert" 'POST' @{ outtype = 'f16'; baseModelConfigDir = $script:BaseDir } 60 }
    }
}
function GameProcs { Get-Process -Name eden -EA 0 }
function GamingActive { (GApi '/api/v1/gaming/status').Body -match '"isGamingActive":\s*true' }

# ---------- run ----------
Say "=== GOS-09 children harness; $(Get-Date -Format s); rounds=$Rounds; children=$($Children -join ',')"
StartCore
$h = (GApi '/api/v1/runtime/status').Body | ConvertFrom-Json
Say ("hardware seen by Core: " + (($h.gpuAdapters | Where-Object { -not $_.isSoftware } | ForEach-Object { $_.name }) -join ', ') + "; text-cuda installed: $((($h.backends | Where-Object { $_.backend -eq 'text-cuda' }).installed))")
$lib = @((GApi "/api/v1/gaming/library").Body | ConvertFrom-Json)
if (-not $GameId) { $GameId = ($lib | Where-Object { $_.emulatorId } | Select-Object -First 1).id }
$game = $lib | Where-Object { $_.id -eq $GameId }
if (-not $game) { throw 'No library entry to launch.' }
Say "game: library id $GameId, emulator '$($game.emulatorId)', non-elevated"
if ($Children -contains 'speech') { SetupSpeech }
if ($Children -contains 'retrieval') { SetupRetrieval }
if ($Children -contains 'lora') { SetupLora }

$results = @()
foreach ($kind in $Children) {
    for ($round = 1; $round -le $Rounds; $round++) {
        $row = [ordered]@{ child = $kind; round = $round; childPids = ''; aliveBeforeGame = $false; goneMs = $null; gamingActive = $false; aiStartRefusedCode = $null; aiStartRefusedText = ''; restartedWhileGaming = $null; restartedAfterExit = $null; verdict = 'FAIL' }
        $job = $null
        try {
            if (GamingActive) { $null = GApi '/api/v1/gaming/exit' 'POST' @{} 60; Start-Sleep 2 }
            $job = BeginWork $kind
            $alive = $null; $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt $ChildAliveTimeoutSec -and -not $alive) { $alive = ChildProcs $kind; if (-not $alive) { Start-Sleep -Milliseconds 100 } }
            if (-not $alive) { $row.verdict = 'FAIL: child never observed alive'; Say "  [$kind r$round] $($row.verdict)"; $results += [pscustomobject]$row; continue }
            $pids = @($alive | ForEach-Object { [int]$_.ProcessId }); $row.childPids = ($pids -join '+'); $row.aliveBeforeGame = $true
            $t0 = [Diagnostics.Stopwatch]::StartNew()
            $launch = GApi "/api/v1/gaming/library/$GameId/launch" 'POST' @{} 60
            Say "  [$kind r$round] child pids $($row.childPids) alive; game launch HTTP $($launch.Code) returned after $($launch.Ms) ms"
            while ($t0.ElapsedMilliseconds -lt 15000 -and (@($pids | Where-Object { Get-Process -Id $_ -EA 0 }).Count -gt 0)) { Start-Sleep -Milliseconds 20 }
            $still = @($pids | Where-Object { Get-Process -Id $_ -EA 0 }).Count
            $row.goneMs = if ($still -eq 0) { $t0.ElapsedMilliseconds } else { -1 }
            $row.gamingActive = [bool](GamingActive)
            $probe = ProbeRefusal $kind
            $row.aiStartRefusedCode = $probe.Code; $row.aiStartRefusedText = ($probe.Body -replace '\s+', ' ').Substring(0, [Math]::Min(140, $probe.Body.Length))
            Start-Sleep -Seconds 3
            $row.restartedWhileGaming = @(ChildProcs $kind).Count -gt 0
            GameProcs | Stop-Process -Force -EA 0
            for ($i = 0; $i -lt 30 -and (GameProcs); $i++) { Start-Sleep -Milliseconds 300 }
            $null = GApi '/api/v1/gaming/exit' 'POST' @{} 60
            for ($i = 0; $i -lt 20 -and (GamingActive); $i++) { Start-Sleep -Milliseconds 500 }
            Start-Sleep -Seconds 4
            $row.restartedAfterExit = @(ChildProcs $kind).Count -gt 0
            $refused = ($row.aiStartRefusedCode -in 409, 503, 423, 403) -and ($probe.Body -match '(?i)gaming')
            $ok = ($row.goneMs -ge 0 -and $row.goneMs -le $ExitBoundMs) -and $row.gamingActive -and $refused -and -not $row.restartedWhileGaming -and -not $row.restartedAfterExit
            $row.verdict = if ($ok) { 'PASS' } else { 'FAIL' }
        } catch { $row.verdict = "FAIL: $($_.Exception.Message)" }
        finally { if ($job) { Stop-Job $job -EA 0; Remove-Job $job -Force -EA 0 } }
        Say ("  [{0} r{1}] {2} goneMs={3} gaming={4} refused={5} '{6}' restartedWhileGaming={7} restartedAfterExit={8}" -f $kind, $round, $row.verdict, $row.goneMs, $row.gamingActive, $row.aiStartRefusedCode, $row.aiStartRefusedText, $row.restartedWhileGaming, $row.restartedAfterExit)
        $results += [pscustomobject]$row
    }
}
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json')
$pass = @($results | Where-Object { $_.verdict -eq 'PASS' }).Count
Say "=== $pass / $($results.Count) PASS"
Stop-Process -Name PaRuX.Server -Force -EA 0
if ($pass -ne $results.Count) { exit 1 }
