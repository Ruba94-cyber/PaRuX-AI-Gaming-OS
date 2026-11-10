# Release readiness

**Verdict: PUBLISHED AS BETA PRE-RELEASE, NOT ALL GATES MET.** `v4.1.0` (commit `348a433c`) was published on GitHub on 2026-11-10 as a pre-release at the owner's instruction, after green xUnit, green FlaUI regression and verified packages. The gates below that are PARTIAL remain open and must not be described as passed. Evidence: [QA_EVIDENCE.md](QA_EVIDENCE.md), [RELEASE_CANDIDATE_4.1.0.md](RELEASE_CANDIDATE_4.1.0.md).

Updated 2026-11-10.

**Post-release source work (NOT part of any published 4.1.0 archive):** `3ddfd0a`, `d6c1abc`, `e88b428`, `31c05b8`, `764fbbe`, `4d14fe0` hardened Corporate Lite packaging, added optional local ONNX reranker and explicit strict offline acceptance, updated the canonical plan and corrected public README status. These changes are not yet compiled or tested on Windows and were not signed or released. The earlier successful xUnit/FlaUI results are for the previous tested tree; they do not automatically validate later commits. The next native gate is a full model-containing Corporate offline ZIP with `-RequireReranker`, clean extracted package and `tests/corporate/measure-corporate-lite.ps1 -EnforceAcceptance`. Passing this smaller corpus test does not certify 30,000-page PDFs, 8 GB machines or enterprise readiness.

| Gate | Status | Basis |
|---|---|---|
| 1 Feature completeness | PARTIAL | 17 plan sections are SOURCE PARTIAL (written remotely, unit-tested only); per-section native checks are listed in `.ai/artifacts/native-verification-matrix.md` (local). Gaming parity gaps GAM-003..007 open |
| 2 UX/UI | PARTIAL | Real-app screenshots reviewed at 200% on 2560x1600 only; scaling 100/125/150%, 4K, TV, multi-monitor not tested (TV/second monitor need the owner's hardware) |
| 3 Stability | PARTIAL | P0 fixed: Core start when Windows reserves port 51123; onboarding profile switch blocked by detection fixed (QA-016) |
| 4 Performance | PARTIAL | [PERFORMANCE_BENCHMARKS.md](PERFORMANCE_BENCHMARKS.md): startup, RTX 5080 inference and Gaming VRAM release measured; in-game FPS, OOM recovery, CPU-only and RAG latency not run; open defects PERF-002/003 in the plan |
| 5 Security | PARTIAL | Static review (QA-017): 0 Critical / 0 High / 7 Medium; SEC-F03/F04/F06 fixed in `d81cf475`; F01, F02, F05 open; .NET 8 reaches end of support in November 2026 (migration not planned in this RC); `dotnet list package --vulnerable` not run |
| 6 Testing | PASS (automated) | At `5cdf56e3`: Server xUnit 13,068 pass / 0 fail (QA-024); FlaUI Regression 309/309 on a fresh publish (QA-025). Earlier full runs showed 1-2 intermittent timeouts (QA-019/022). Real-app verification of the 17 SOURCE PARTIAL sections (REL-006) not done |
| 7 Packaging (MSI, Portable, Portable Corporate, APK) | PARTIAL | All four built locally as 4.1.0 ([RELEASE_CANDIDATE_4.1.0.md](RELEASE_CANDIDATE_4.1.0.md)); Portable/Corporate launch and relocation verified, APK runs on the emulator. MSI install/upgrade/uninstall NOT RUN (needs elevation by the owner); no clean-machine test. Corporate Lite is published both with the bundled Qwen3-4B model (owner-confirmed, chat verified on CPU, QA-026) and without a model |
| 8 Signing | PARTIAL | MSI Authenticode-signed with the owner's self-signed certificate and update envelope verified; APK signed with the release keystore (`apksigner` OK). Portable and Corporate zips and their EXEs are not signed (no signing step in the scripts). Self-signed certificate means SmartScreen warnings |
| 9 Documentation | PARTIAL | QA evidence, security review and this file current; README/feature matrix/release notes not refreshed |
| 10 Public readiness | NOT RUN | No screenshots or benchmark claims prepared; none may be asserted without evidence |

## Blocked on the owner's hardware
- TV 4K, second monitor, physical controllers, eGPU disconnect/reconnect, a low-spec PC for the Corporate profile, a physical Android device.

## Authorizations still needed
- Final approval before any tag push, GitHub release or distribution of installers.
