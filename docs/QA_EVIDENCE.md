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
| QA-012 | FlaUI Regression 3 (fresh publish of `d5822aaf`) | in progress | - | - | NOT YET RECORDED | - |

## Known open QA items (not resolved)

- The 19 QA-010 failures: Console/TV (tests now select the Gaming tab first), Onboarding card/idle state, Chat history/resize, Gaming-only profile restart, Model Hub layout bounds at 560x560, Settings scroll, tile arrow-key navigation, manual library add/remove. To be triaged from QA-012.
- Not run at all yet: Android Gradle build and tests, MSI/Portable/Portable Corporate packaging and clean-install tests, code signing verification, GPU/TTFT/tokens-per-second benchmarks, Gaming FPS regression, controller hardware tests, 4K/TV/multi-monitor and 100/125/150% scaling passes, Corporate low-spec profile test.
