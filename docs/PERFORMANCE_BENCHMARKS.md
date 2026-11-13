# PaRuX performance benchmarks (gate PERF-001)

- Date: 2026-11-10
- Commit measured: `d81cf475` (main) plus uncommitted harness fixes listed under "Harness fixes"
- Every number below was measured on this PC. Nothing is estimated. Anything that could not be measured is listed under "NOT RUN".
- Raw outputs: `.ai/artifacts/perf-rc1/` (git-ignored working evidence).

## Hardware and software

| Item | Value |
| --- | --- |
| CPU | AMD Ryzen 7 8840U (16 logical CPUs) |
| RAM | 25.8 GB visible |
| Discrete GPU | NVIDIA GeForce RTX 5080, 16303 MiB, driver 621.01, connected as an external GPU (eGPU) |
| Integrated GPU | AMD Radeon 780M (not used by the text runtime) |
| Display | 2560x1600 at 200% |
| OS | Windows 11 Home 10.0.26320 |
| Builds | `dotnet build src/PaRuX.Server -c Release`, `dotnet build src/PaRuX.App -c Release -p:Platform=x64` (both 0 errors) |
| Text runtime | llama.cpp b12241 CUDA 13.5 (`llama-server.exe`) |
| Model | Qwen3-4B Q4_K_M GGUF, 16384 context, q8_0 KV cache, flash attention on |

Idle GPU baseline on this machine is 261 MiB used.

## 1. Startup and idle (isolated throw-away data roots)

Commands:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\measure-startup.ps1 -Mode all -Runs 3 -IdleSeconds 60 -Json .ai\artifacts\perf-rc1\startup.json
powershell -NoProfile -ExecutionPolicy Bypass -File tools\measure-startup.ps1 -Mode app -Runs 3 -IdleSeconds 60 -Json .ai\artifacts\perf-rc1\startup-app.json
```

The first command produced valid Core numbers; its App half was invalid because of the window-title matching defect fixed afterwards (see Harness fixes), so the App numbers come from the second command. The Core table is from the first command.

Core alone (process start to `GET /api/v1/health` 200, then 60 s idle):

| Metric | Run 1 | Run 2 | Run 3 | Median | Notes |
| --- | --- | --- | --- | --- | --- |
| Core ready (ms) | 6231 | 1442 | 1482 | 1482 | Run 1 was the first start after a rebuild (cold file cache). |
| Core working set, idle (MB) | 185 | 197 | 202.3 | 197 | |
| Core private bytes, idle (MB) | 129.6 | 142 | 147 | 142 | |
| Core CPU, idle (% of all logical CPUs) | 0.28 | 0.332 | 0.303 | 0.303 | Second half of the idle window only. |

App plus Core (App process start to main window with an enabled Edit control, 60 s idle):

| Metric | Run 1 | Run 2 | Run 3 | Median | Notes |
| --- | --- | --- | --- | --- | --- |
| App interactive (ms) | 1459 | 1430 | 1558 | 1459 | Window found by UI Automation with enabled chat input. |
| Core healthy after App start (ms) | 1815 | 1744 | 1970 | 1815 | |
| App working set, idle (MB) | 256.5 | 257.2 | 255.6 | 256.5 | |
| App private bytes, idle (MB) | 152.4 | 151.4 | 151.3 | 151.4 | |
| App CPU, idle (%) | 0.055 | 0.039 | 0.046 | 0.046 | |
| Core working set with App (MB) | 229.8 | 226.9 | 230.5 | 229.8 | |
| Core private bytes with App (MB) | 168.6 | 166.2 | 169 | 168.6 | |
| Core CPU with App, idle (%) | 0.234 | 0.241 | 0.283 | 0.241 | |

## 2. AI inference on the RTX 5080

Data root: `.ai/artifacts/hw-data3` (already onboarded; llama.cpp CUDA runtime and Qwen3-4B Q4_K_M present). Core started with `PARUX_DATA_ROOT=<hw-data3>` and `PARUX_LOCALAI_STATE_DIR=<hw-data3>\localai-state`; endpoint and token read from `<hw-data3>\server\bootstrap.json` (the Core selects a free loopback port because 51099-51198 is OS-reserved).

Commands:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\benchmark-gpu.ps1 -BaseUrl <endpoint> -Token <token> -OutputDirectory .ai\artifacts\perf-rc1\gpu -Repetitions 5 -Phases detection,backend,load,throughput,longcontext,concurrency,unload
pwsh -NoProfile -ExecutionPolicy Bypass -File .ai\artifacts\perf-rc1\gpu-evidence.ps1 -Out .ai\artifacts\perf-rc1\gpu-evidence-pass1.json     (pass 1: 3 runs)
pwsh -NoProfile -ExecutionPolicy Bypass -File .ai\artifacts\perf-rc1\gpu-evidence.ps1 -Out .ai\artifacts\perf-rc1\gpu-evidence-pass2.json     (pass 2: 3 runs)
```

`scripts\benchmark-gpu.ps1` needs PowerShell 7 (it uses `Start-ThreadJob` and `Process.Kill(bool)`); under Windows PowerShell 5.1 it stops immediately because `System.Net.Http.HttpClient` is not loaded. `gpu-evidence.ps1` is a per-run harness written for this gate (kept beside the raw outputs, not part of the product): each run does Off, waits for `llama-server` to exit, records VRAM, then `Standard`, waits for the runtime to report healthy, records the llama-server command line and memory, then streams 3 chat completions (256 max tokens, prompt "Write a detailed paragraph explaining how a CPU cache works.") while `nvidia-smi` is sampled every 200 ms. Six runs in total (2 passes x 3). The first run of each pass started from an already-unloaded runtime, so its VRAM-release value is not meaningful and is excluded from that row.

### 2a. Runtime start, load, TTFT, throughput (6 runs, `gpu-evidence.ps1`)

| Metric | Per-run values | Median | Notes |
| --- | --- | --- | --- |
| Runtime start to healthy, `Standard` action (ms) | 2309, 1768, 1742, 1776, 1745, 1765 | 1766.5 | Includes llama-server launch and model load of a 2.3 GiB Q4_K_M model file. The 2309 and the first-ever 4085 (benchmark-gpu attempt, cold file cache) are the slowest observed. |
| TTFT, first request after load (ms) | 232, 212, 296, 227, 202, 202 | 219.5 | First streamed content token as seen by the client through Core. |
| TTFT, warm requests (ms) | 12 values: 74 to 196 | 147 | Requests 2 and 3 of each run. Minimum 74, maximum 196. |
| Decode throughput (tokens/s) | 18 values: 192.1 to 199.3 | 195.8 | One streamed content chunk is counted as one token; first token excluded. |
| Peak GPU utilisation during generation (%) | 89, 89, 89, 89, 89, 96 | 89 | `nvidia-smi`, 200 ms sampling. |
| Peak board power (W) | 257 to 270 | about 265 | |
| Peak SM clock (MHz) | 2872 to 2887 | about 2880 | |

`scripts\benchmark-gpu.ps1` (5 repetitions, 128 max tokens, prompt "Explain in three sentences why prompt caching reduces time to first token."): TTFT p50 77 ms, p95 228 ms, decode p50 192.35 tokens/s, peak GPU utilisation 96%, peak VRAM 4328 MiB, peak temperature 66 C.

### 2b. Memory, VRAM and proof that the GPU is used

| Metric | Value | Notes |
| --- | --- | --- |
| llama-server command line | `--gpu-layers all --fit on --fit-target 1024 --split-mode none --main-gpu 0`, executable `LocalAI\runtime\llama.cpp-b12241-cuda13.5\llama-server.exe` | Identical in all 6 runs. No `--device` pin. |
| VRAM idle (MiB) | 261 | `nvidia-smi` |
| VRAM with model loaded (MiB) | 4318 (peak 4328 during generation) | All 6 runs. Model adds about 4057 MiB. |
| GPU activity during generation | utilisation 89 to 96%, power 257 to 270 W, SM clock about 2880 MHz | 13 to 16 of 15 to 17 samples per run had utilisation above 50%. Idle is 0% / 261 MiB. |
| VRAM released by `Off` (MiB) | 4067 in 4 of 4 valid runs; llama-server process gone after 110, 118, 133 ms | VRAM returned to 261 MiB each time. |
| VRAM released by `benchmark-gpu` unload phase | 4330 to 261 MiB (94%) | |
| llama-server RAM | working set about 838 MB; private bytes about 5.2 GB | The private figure includes CUDA/driver mappings. System free RAM fell by about 0.7 to 1.0 GB after load. |
| PaRuX runtime status vs `nvidia-smi` | PaRuX `/api/v1/runtime/status`: 16303 MiB total, 11662 MiB free after load (4641 MiB used). `nvidia-smi`: 4318 MiB used. | PaRuX reports 323 MiB (about 7%) more than `nvidia-smi`; the two agree on GPU present, total VRAM and the active profile ("Qwen3-4B-Q4_K_M Standard GPU"). `benchmark-gpu` detection phase: "PaRuX agrees with the hardware". |

Conclusion on GPU use: the runtime is launched with all layers on the GPU, VRAM rises by about 4 GB on load and falls back to idle on unload, and the GPU shows 89 to 96% utilisation at about 265 W while generating. No silent CPU fallback was observed.

### 2c. Other `benchmark-gpu.ps1` phases

| Phase | Result |
| --- | --- |
| Concurrent requests | 4 requested, 4 succeeded, 0 failed, max 3893 ms (single slot serialises them). |
| Long context (24000 characters) | TTFT 7799 ms, peak VRAM 14090 MiB. NOT comparable to the 4B figures: Core's automatic router switched this request to Qwen3-Coder-30B (confirmed separately: llama-server was running `Qwen3-Coder-30B-A3B-UD-Q3_K_XL.gguf`, still `--gpu-layers all`, VRAM 14090 MiB; the one-off request took 10.9 s including that model load). |
| Warm load (`Standard` while already running) | 1766 to 1811 ms and the llama-server PID changes: that measurement predates the warm-reuse fix (commit 5cdf56e3); current code reuses an identical healthy runtime (see defect 2). Needs a real-GPU re-measure. |

## 3. Gaming transition

Commands:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File tools\bench-gaming-transition.ps1 -OutFile .ai\artifacts\perf-rc1\gaming-transition-stub.json
pwsh -NoProfile -File .ai\artifacts\perf-rc1\gm3.ps1      (real runtime: Core + loaded model, no game; actions GameStart / GameStop)
```

`tools\bench-gaming-transition.ps1` runs `GamingTransitionBenchmarkTests`, which uses stub launcher/input providers: it measures PaRuX orchestration overhead only (20 cycles).

| Metric (stub providers, 20 cycles) | p50 | p95 | max |
| --- | --- | --- | --- |
| Enter Gaming (ms) | 2.99 | 3.97 | 54.07 |
| Exit Gaming (ms) | 0.60 | 0.72 | 4.26 |

Real runtime transition (Core only, Qwen3-4B loaded on the RTX 5080, `GameStart` and `GameStop` runtime actions; no game was launched):

| Metric | Run 1 | Run 2 | Run 3 | Median | Notes |
| --- | --- | --- | --- | --- | --- |
| `GameStart` action returns (ms) | 114 | 106 | 104 | 106 | |
| llama-server gone after `GameStart` (ms) | 133 | 114 | 112 | 114 | |
| VRAM loaded to in-game (MiB) | 4318 to 261 | 4318 to 261 | 4318 to 261 | 4057 released | Back to idle baseline. |
| `Standard` (start AI) refused during Gaming | yes (409) | yes (409) | yes (409) | yes | Chat requests are refused too. Hard-off holds. |
| `GameStop` action returns (ms) | 4 | 2 | 2 | 2 | |
| Text runtime restored automatically within 15 s | no | no | no | no | With Core only, nothing relaunches the model after `GameStop`; an explicit `Standard` is needed. |
| Explicit reload to healthy after `GameStop` (ms) | 1762 | 1746 | 1763 | 1762 | |

## Harness fixes made (uncommitted)

- `tools/measure-startup.ps1`: health probe read the fixed port 51123 (OS-reserved here); it now reads the endpoint from `<data root>\server\bootstrap.json`. The App window match required the exact title `PaRuX AI & Gaming OS`, but UI Automation reports it as `PaRuX AI  Gaming OS` (no ampersand), so `interactiveMs` was never found; the match now allows the optional ampersand.
- `scripts/benchmark-gpu.ps1`: (1) the VRAM sampler used `Task.Run` with a script block, which is ambiguous on PowerShell 7 and has no runspace on a pool thread, so the load, throughput and long-context phases errored; it is now a thread job calling `nvidia-smi`. (2) The chat stream parser threw under strict mode on Core's `parux_event` frames (no `choices`) and on empty `delta` objects. (3) The installed-builds query now passes the required `backend` parameter (it returned 400).
- `tools/bench-gaming-transition.ps1`: a relative `-OutFile` was resolved against the test's bin folder and the test failed; it is now made absolute first.

## Methodology and limits

- 3 runs for startup and the real-runtime Gaming transition; 6 runs (2 passes x 3) for load, TTFT and throughput; `benchmark-gpu.ps1` itself used 5 repetitions. Medians are the lower middle value for the startup script and the standard median for the Python-summarised GPU runs. Small samples: treat the spread, not only the median.
- One model only (Qwen3-4B Q4_K_M), one prompt family, 256 (evidence) or 128 (benchmark-gpu) max tokens, 16384 context. No larger-model throughput was measured in a controlled way.
- The RTX 5080 is an external GPU: PCIe/Thunderbolt-class link bandwidth affects load time, and nothing here applies to an internal GPU.
- Chunks are counted as tokens; tokens/s is therefore approximate to the extent a chunk differs from a token.
- Startup runs were done on a quiet desktop with other applications (browsers, editors) open; those were not closed.
- Startup measures use an isolated throw-away data root with onboarding completed and no model; they do not reflect a heavily populated user profile.
- Core-only Gaming transitions do not include game launch, anti-cheat, Game Mode of the OS or any real game.
- The `benchmark-gpu.ps1` phases `switch` (no second model profile is installed: `Uncensored` returned 409), `oom` (no library model larger than 1.5x VRAM) and `gamemode` (Core-side hard-off, covered by section 3 instead) were not used for numbers; the early full-phase runs also left the runtime stopped between phases, so only the phases listed above were run in the final pass.

## NOT RUN

| Item | Reason |
| --- | --- |
| In-game FPS regression with PaRuX running | Needs a real game session; no game launched, by instruction. |
| `benchmark-gpu` `switch` phase | No second text profile installed (`Uncensored` start returned 409); nothing to switch to. No download allowed. |
| `benchmark-gpu` `oom` phase | No managed model larger than 1.5x the 16 GB VRAM in the library (skipped by the script). |
| CPU-only inference comparison (`-CpuOnly`) | Requires relaunching the runtime on CPU; outside this gate and would not be evidence of the GPU path. |
| Vulkan / Radeon 780M inference | Not part of the requested scope. |
| RAG indexing and query latency | No harness for RAG index/query latency that works on synthetic data without an embedding model was found under `scripts/` or `tools/` (`scripts/benchmark-virtual-context.ps1` reads the default LocalAppData bootstrap and measures context assembly, not RAG; the lexical retrieval test measures quality, not latency), and the isolated data root holds no embedding model. No model may be downloaded. |
| Other model sizes, multi-GPU, battery power | Out of scope for this gate. |
| Windows-installed (MSI/portable) startup | Only Release build outputs were measured. |

## Performance defects and observations

1. Idle runaway in Core, not reproduced (needs investigation). After the first full `benchmark-gpu.ps1` passes, one Core process (isolated data root, no client connected) was observed with working set 3.1 to 6.5 GB, private bytes up to about 8.5 GB and about 100% of one core for over 450 CPU-seconds while idle. During that state a model start was refused by resource admission ("system RAM 89.6% > 86%"). Five later controlled attempts (non-streamed and streamed chats, four Off/Standard cycles, four concurrent requests, long-context routing to the 30B model, `GameStart`/`GameStop`) kept Core at about 0.4 GB and about 0 CPU idle, so the trigger is unidentified. Evidence: `core-memory-probe.log`, `core-memory-probe2.log`, `core-runaway-monitor*.log`.
2. `Standard` re-request while already serving: RESOLVED IN CODE (PERF-003a), real-GPU re-measure pending. The 1.8 s restart above was measured before commit 5cdf56e3; `WarmTextReusePolicy` now answers an identical request from the resident llama-server ("Already running") when the model file, executable and every launch argument match and the process is healthy. The HANDOFF 2026-11-10 v4.1.0 live check (same PID) is consistent with that. Unit-tested through the real start path with a stubbed launch (`WarmReuseAndGamingRestoreTests`: identical request, respelled model path and changed mode launch once; a different model or a forced restart launch again; different context or GPU layers are a different launch in `WarmTextReuseTests`). Not yet measured on a GPU: same llama-server PID and the warm-load timing on the RTX 5080.
3. Automatic model routing can swap a long prompt to a larger model (Qwen3-Coder-30B, 14 GB VRAM, 7.8 to 10.9 s to first token) without the caller asking. BOUNDED AND EXPLAINED IN CODE (PERF-003b), real-GPU re-measure pending. `AutoEscalationPolicy` narrows the AUTO pool before ranking: it never offers a larger model whose estimated need exceeds 90% of the router's memory budget, keeps the loaded model when a larger one gains under 10% capability (not for the Max intelligence and Custom profiles), and never escalates on Corporate or CPU-only hosts. The reason ("Escalated from X to Y ..." / "Stayed on X ..." / "Skipped Y ...") is appended to the route decision's `SelectionReason`, which the `model_routed` event (`reason`) and the Model Routing panel already show. Manual roles, exact/scoped overrides and Quality/Performance/LowVram modes never reach it. Unit-tested (`AutoEscalationPolicyTests`, 17 cases). Not measured: the memory budget is the router's fit estimate (total VRAM plus the current RAM budget), not live free VRAM; the live admission check at load time stays the final authority. Whether the 24000-character prompt now stays on the 4B model depends on the measured capability scores on the RTX 5080 and was not re-run.
4. `GameStop` with only Core running does not relaunch the text runtime: INTENDED, now consistent and honest (PERF-003c). Gaming hard-off is followed by lazy recovery, the same rule as speech and retrieval: leaving Gaming loads nothing heavy by itself. The model that was loaded is remembered across the hard-off (state directory file, survives a Core restart), the next request (Ensure/Auto/On) reloads exactly that model with no error, `GET` runtime status reports `textUnloadedForGaming` and `textReloadOnDemandModelPath`, and the status line reads "unloaded for Gaming (<model>); will reload on demand". The only automatic reload is the existing always-on Local AI mode (`ON`), started in the background when Gaming ends; `AUTO` and `OFF` never preload. Nothing reloads while a game is active. Unit-tested (`WarmReuseAndGamingRestoreTests`). Not measured: the real reload latency after a game and VRAM at that point on the RTX 5080; the App-side startup warm-up setting is separate and unchanged.
5. PaRuX's VRAM figure differs from `nvidia-smi` by about 323 MiB (7%) while a model is loaded.
6. `scripts/benchmark-gpu.ps1` fails against PowerShell 5.1 (documented requirement should be PowerShell 7).
