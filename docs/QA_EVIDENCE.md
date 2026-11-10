# QA evidence log

Only results actually executed on the reference PC are recorded here. Anything not listed was **not run**.

**Reference PC (2026-11-09):** Windows 11 (10.0.26320), AMD Ryzen 7 8840U, NVIDIA GeForce RTX 5080 16 GB (driver 621.01, eGPU) + Radeon 780M, 25.8 GB RAM visible, display 2560x1600 at 200% (1280x800 DIP, the "13-inch laptop" case), .NET SDK 8.0.424.

| ID | Test | Environment / version | Expected | Actual | Result | Evidence |
|---|---|---|---|---|---|---|
| QA-001 | Release build of `PaRuX.sln` on merged `main` (646 source-only commits) | Release, `441bc3a7` | 0 errors | 6 independent compile errors | FAIL then fixed | `b070fab5` |
| QA-002 | Release build after fixes | `b070fab5` | 0 errors | 0 errors, 11 warnings | PASS | build log |
| QA-003 | `PaRuX.Server.Tests` full xUnit | `b070fab5`, 27 min | all pass | 13,010 pass / 9 fail / 9 skipped of 13,028 | FAIL then fixed | `.ai/testresults/server.trx` (local, untracked) |
| QA-004 | The 9 failing classes re-run | `788dc74e` | all pass | 8 classes green (all 9 failures were stale assertions after ORPO/KTO training shipped, wording and Android policy moves) | PASS | commit `788dc74e` |
| QA-005 | FlaUI Smoke on real published app | `2ed81c4e` | 35/35 | 30 pass / 5 fail | FAIL | `.ai/flaui-smoke.log` |
| QA-006 | Same failures against v4.0.1 build | tag `v4.0.1` | n/a (baseline) | the same 3 tests re-run fail identically | Pre-existing, not a regression | baseline run |
| QA-007 | Root cause of QA-005/006 | real app | Core server starts | `PaRuX Server startup timed out. Bootstrap=missing`; Kestrel `SocketException 10013` on 127.0.0.1:51123; Windows TCP excluded range `51099-51198` covers the port (Hyper-V/WSL2/Docker) | **Product bug (P0)** | server run output |
| QA-008 | Port fallback fix | `3997fb3f` | Core binds a free loopback port and publishes it in the bootstrap file | 2 new xUnit tests pass | PASS (unit) | `CorePortSelectionTests` |
| QA-009 | FlaUI Smoke after fix | `3997fb3f` published app | 35/35 | 35 pass / 0 fail (2 m 9 s) | PASS | `.ai/flaui-smoke2.log` |
| QA-010 | FlaUI Regression (`Category!=HardwareE2E`) after fix | `3997fb3f` | all pass | 289 pass / 19 fail of 308 (25 m 22 s) | PARTIAL | `.ai/flaui-regression2.log` |
| QA-011 | New Gaming completion status/score unit tests | `d5822aaf` | pass | 76 related tests pass; first run found a real bug (state clone dropped the new fields), fixed | PASS | `GameCompletionStatusTests` |
| QA-012 | FlaUI Regression 3 (fresh publish of `d5822aaf`) | `d5822aaf` | all pass | 297 pass / 11 fail of 308 (23 m 11 s) | PARTIAL | `.ai/flaui-regression3.log` |
| QA-013 | `#folder` mention + Document search drag-and-drop: build and targeted xUnit | `f420f347` | 0 errors, all pass | Debug x64 build 0 errors; 34 targeted tests pass. Real-app drag-and-drop and a live `#mention` chat round trip NOT run | PASS (unit only) | `DocumentMentionPolicyTests` |
| QA-014 | Full `PaRuX.Server.Tests` re-run after all test fixes | `f420f347` + working tree of `031baa84`, 27 m 18 s | all pass | 13,036 pass / 0 fail / 9 skipped of 13,045 | PASS | `.ai/testresults/server-rc1.trx` (local) |
| QA-015 | FlaUI Regression on a fresh publish | working tree of `031baa84` | all pass | 273 pass / 35 fail of 308 (30 m 41 s). 31 failures are Gaming tests that still expect controls on the default section after the Library/Downloads/Add & import/Devices split (`15c1d6fc`); 4 assertion failures (Model Hub Start board, explicit discovery refresh, onboarding idle state, one Console test) | PARTIAL, triage in progress | `.ai/artifacts/flaui-regression-rc1.log` |
| QA-019 | FlaUI Regression after fixing QA-015 (fresh publish) | working tree of this commit, 23 m 16 s | all pass | 307 pass / 1 fail of 308. The one failure (`DocumentSkillsAndRagWipeUiTests.Contradictions_skill_button...`, timeout on `Chat_Documents_Switch`) passes 3/3 twice when its class is re-run alone on the same build: intermittent, also seen in QA-012, cause not yet found | PARTIAL (1 intermittent) | `.ai/artifacts/flaui-regression-rc2.log` |
| QA-021 | Full Server xUnit after security remediation | `d81cf475` working tree, 28 m 52 s | all pass | 13,057 pass / 1 fail / 9 skipped of 13,067; the failure was a source-contract assertion made stale by `b8d720ae`, fixed in `d81cf475` and re-run with 176 targeted tests green. Full suite not re-run after that one-line test fix | PASS (with targeted re-run) | `.ai/artifacts/sec-fix-*.log` |
| QA-022 | FlaUI Regression on a fresh publish after security changes | `1137d918`, 22 m 19 s | all pass | 306 pass / 2 fail of 308 (`SetupProfileRuntimeUiTests.Gaming_only_choice_survives_a_restart...`, `ResponsiveLayoutCoverageTests.ModelHub_layout...`, both timeouts). Both classes plus the QA-019 one re-run alone twice on the same build: 13/13 both times. The suite therefore has 1-2 different intermittent timeouts per full run; not a deterministic product failure, root cause open | PARTIAL (intermittent) | `.ai/artifacts/flaui-regression-rc3.log` |
| QA-023 | Performance benchmarks | `d81cf475` | measured | see [PERFORMANCE_BENCHMARKS.md](PERFORMANCE_BENCHMARKS.md) | MEASURED (partial scope) | `.ai/artifacts/perf-rc1/` |
| QA-024 | Full Server xUnit on the release candidate | `5cdf56e3` working tree | all pass | 13,068 pass / 0 fail / 9 skipped of 13,077 | PASS | `.ai/artifacts/batch3-*.log` |
| QA-025 | FlaUI Regression on a fresh publish of the release candidate | `5cdf56e3`, 22 m 45 s | all pass | 309 pass / 0 fail of 309 | PASS | `.ai/artifacts/flaui-regression-rc4.log` |
| QA-020 | Product bugs found by QA-015 triage | this commit | - | (a) leaving Model Hub cancelled a user-started repository check - now it completes and reports on return; (b) first run always expanded "Manual setup" - now only when returning from Model Hub. Both covered by FlaUI tests passing in QA-019 | FAIL then fixed | `ExplicitDiscoveryTests`, `AutoSetupCoverageTests` |
| QA-016 | Onboarding: choose "Gaming only" while first-run component detection is running | `031baa84` | switch accepted | Was refused with "Finish the current check" (product bug: read-only detection set the busy flag); fixed, FlaUI test passes in QA-015 | FAIL then fixed | `SetupProfileCoverageTests` |
| QA-017 | Static security review | `f420f347` | no Critical/High | 0 Critical / 0 High / 7 Medium / 10 Low / 3 Info; nothing remediated yet; static reading only, several areas not reviewed | REVIEWED, not remediated | [SECURITY_REVIEW.md](SECURITY_REVIEW.md) |
| QA-018 | Agent worktree / branch reconciliation | `main` | no unmerged work lost | all 26 `worktree-agent-*` branches have 0 patches missing from `main` (`git cherry`); the `remote/l*` branches differ by commit identity (squash-merged per `BRANCH_RECONCILIATION_20261105.md`, not re-checked patch by patch) | CHECKED | `git cherry main <branch>` |

## Manual real-app session (2026-11-09, Debug build of `main`, isolated data root, 2560x1600 at 200%)

Driven by hand through the real desktop UI (screenshots reviewed), not by automation. Results are what was observed, not a full pass.

| ID | Flow | Observed | Result |
|---|---|---|---|
| MAN-001 | Default language | UI was a mix of Italian/English because it followed the Windows language; English is now the default, Italian selectable in Settings > Language (hint text switches both ways; full Italian rendering after restart not re-checked) | FIXED, partly checked |
| MAN-002 | Gaming tab layout | One endless page with ~17 toolbar controls; library below the fold; filter rail stacked above the library below ~900 DIP. Now sections Library / Downloads / Add & import / Devices & session, slim toolbar with Filters/More menus, rail kept beside the library on laptop widths | FIXED, visually checked |
| MAN-003 | Steam discovery | The only "game" found was "Steamworks Common Redistributables" | FIXED (unit test), library now correctly empty on this PC |
| MAN-004 | Add a local game, Play | Manual entry added; launch needs explicit confirmation; Notepad launched; Return to AI removed the Gaming marker but the message said "could not be confirmed" when no AI model existed | message FIXED (not re-run) |
| MAN-005 | Display/controller checks | "Inspect connected displays" and "Check controllers" run; no XInput controller detected (none connected) | OBSERVED |
| MAN-006 | Cover art from the internet | Feature exists but was off by default and hidden in a menu | opt-in banner added, NOT yet exercised against the network |
| MAN-007 | Model Hub "Set up the chat engine" | Led to generic Settings (dead end) | FIXED: opens Runtime Manager |
| MAN-008 | Install llama.cpp CUDA (b12233, 569.6 MB) via Runtime Manager | Downloaded, extracted, RTX 5080 shown as `text-cuda` 15.6 GB | PASS |
| MAN-009 | Download Qwen3-4B-Q4_K_M.gguf (2.3 GB) via Start board | Download plan reviewed (hardware fit "Excellent full-GPU fit"), completed | PASS |
| MAN-010 | First chat after download | HTTP 503 "File not found Qwen3.8-27B-UD-IQ4_XS.gguf" while the Start board said Ready (role not assigned) | FAIL then FIXED (auto-assign) |
| MAN-011 | Chat reply on the RTX 5080 | Header "Qwen 4B . Standard", streamed answer received. No latency or tokens/s measured | PASS (functional only) |
| MAN-012 | Create > image setup (~24.2 GB) | Started, downloading ComfyUI | IN PROGRESS |

## Known open QA items (not resolved)

- The 35 QA-015 FlaUI failures (see that row); the earlier QA-010/012 failures are superseded by it.
- Security findings SEC-F01..F07 (Medium) in SECURITY_REVIEW.md are open.
- Not run at all yet: Android Gradle build and tests, MSI/Portable/Portable Corporate packaging and clean-install tests, code signing verification, GPU/TTFT/tokens-per-second benchmarks, Gaming FPS regression, controller hardware tests, 4K/TV/multi-monitor and 100/125/150% scaling passes, Corporate low-spec profile test.
