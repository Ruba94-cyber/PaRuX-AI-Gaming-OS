# PaRuX requirement matrix
Date: 2026-11-11. Reference PC: Windows, RTX 5080 + Ryzen 7 8840U (evidence 10-11 November 2026).
Allowed values: YES / PARTIAL / NO / NOT RUN / N/A / BLOCKED-HW / SIMULATED. NOT RUN = not executed on the reference PC; BLOCKED-HW = needs hardware not available; N/A = not applicable to the row.
A YES in Build/Unit Tests is not a claim that the feature works for a user; see UI Tests and Hardware Tests.
EV-01: Release x64 solution build, 0 errors, at commit 206440a9 (includes all post-beta source tranches and the Codex continuation commits).
EV-02: Full Server xUnit at 206440a9: 13,123 passed / 0 failed / 9 skipped.
EV-03: Android companion unit tests (Gradle testDebugUnitTest): 41/41.
EV-04: FlaUI Smoke 35/35 at the Codex continuation (exclusive run). Full FlaUI Regression has NOT been run on the current source.
EV-05: Standard Portable ZIP 4.1.0 built on the reference PC with -FetchAssets -Smoke (2.69 GB, 1250 entries, SHA-256 inventory verified, direct-EXE smoke OK, CPU and Vulkan loaders and ONNX engines load, Microsoft Visual C++ runtime bundled app-local). Not verified: real chat/RAG from the Standard ZIP, relocation, clean PC.
EV-06: Corporate Lite Offline ZIP with bundled Qwen3-4B Q4_K_M, CPU llama.cpp, embedding and reranker ONNX, VC++ runtime (2.66 GB), built and smoke OK. Real CPU run from the extracted package: 319 files indexed in 63 s; grounded chat 4/4; TTFT 13-20 s; 12-14 tokens/s; peak RAM 6.7 GB. Strict gate NOT yet passed end-to-end on the final source (re-run pending). Not verified: clean PC without developer tools, 8-16 GB machine, 30,000-page PDF, network physically disabled.
EV-07: Hidden prerequisite found and fixed (commit 9973463d): portable ZIPs lacked the VC++ runtime; Corporate Core now stops with a readable reason when it cannot protect its token; Standard Portable protects the token best-effort.
EV-08: Source only, unit-tested only (not run on the reference PC as a user feature): PaRuX.exe --exit clean exit (2856dafd); cross-encoder reranking in the primary document index and spreadsheet column headers (5b79ade7); first batch of mixed Italian/English UI fixes (3314f4a6).
EV-09: Strict Corporate acceptance (tests/corporate/measure-corporate-lite.ps1 -EnforceAcceptance) PASSED on the reference PC against the package built from aaf09c8e, CPU only, document mode: 319 mixed files indexed in 60 s (1,938 chunks); exact lookup 50/50 p95 5 ms; semantic 50/50 p95 25 ms; hybrid with cross-encoder p95 350 ms; 4/4 sentinel facts answered with the right source cited; planted instruction ignored; unsupported question declined without a model call; first token 15-16 s, 13.6 tokens/s, peak RAM 6.3 GB; 0 non-loopback connections observed; clean exit through PaRuX.exe --exit with no process left. Synthetic corpus only - not a clean-machine, low-RAM, large-PDF or physically offline certification.

| Requirement | Implemented | Integrated | Build | Unit Tests | UI Tests | Hardware Tests | Release Ready | Evidence |
|---|---|---|---|---|---|---|---|---|
| REL-001 Whole solution compiles | YES | YES | YES | N/A | N/A | N/A | YES | QA-002, EV-01 |
| REL-002 Server xUnit green | YES | YES | YES | YES | N/A | N/A | YES | QA-014, QA-024, EV-01, EV-02 |
| REL-003 Core port selection | YES | YES | YES | YES | YES | N/A | YES | QA-007, QA-008, QA-009 |
| REL-004 FlaUI Regression non-hardware | YES | YES | YES | N/A | PARTIAL | N/A | NO | QA-010, QA-019, QA-022, QA-025, EV-04. Note: Smoke only on current source; last full regression 309/309 was at 5cdf56e3 |
| REL-005 AI/Gaming tab >= 44 px | YES | PARTIAL | NOT RUN | N/A | NOT RUN | N/A | NO | none |
| REL-006 Real-app verification of 17 SOURCE PARTIAL sections | NO | NO | NOT RUN | YES | NOT RUN | BLOCKED-HW | NO | none (QA-014 unit classes only) |
| UX-002 Return to AI reachable from every Gaming section | NO | NO | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GAM-001 Completion status and 0-10 score | YES | PARTIAL | YES | YES | NOT RUN | N/A | NO | QA-011 |
| GAM-002 Compact Gaming toolbar, covers above fold | YES | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GAM-003 First-run store scan prompt | YES | YES | YES | YES | PARTIAL | N/A | NO | QA-035: opt-out path verified in the real UI (banner shown, "Not now" persisted); the scan path against real Steam/Epic/GOG installs NOT RUN |
| GAM-004 Cover control, virtualization for 2,000 titles | NO | NO | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| GAM-005 Save-folder detection and backup | NO | NO | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GAM-006 Update detection, export, itch.io, achievements | NO | NO | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GAM-007 Sunshine/Moonlight, HDR/VRR | NO | NO | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| PKG-001 MSI, Portable, Corporate, APK built and signed | YES | YES | YES | N/A | NOT RUN | BLOCKED-HW | NO | docs/RELEASE_CANDIDATE_4.1.0.md, QA-026 (plan row cites stale c288afb9), EV-05, EV-06 |
| PKG-002 Health reports product version | YES | NO | NOT RUN | NOT RUN | NOT RUN | N/A | NO | docs/RELEASE_CANDIDATE_4.1.0.md (health shows 4.1.0; Implemented per RC doc; earlier "NOT IMPLEMENTED" superseded) |
| PKG-STD Standard Portable standalone ZIP | YES | YES | YES | YES | NOT RUN | PARTIAL | NO | EV-05, EV-07, QA-036 (chat, document search with citation, abstention, clean exit, folder relocation and no host-profile writes verified on the build PC). Not verified: clean PC, network disabled, non-NTFS volume, the UI through FlaUI on the packaged app |
| PKG-CORP Corporate Portable standalone ZIP with model | YES | YES | YES | YES | NOT RUN | PARTIAL | NO | EV-06, EV-07, EV-09 (strict gate PASS on the reference PC). Not verified: clean PC without developer tools, 8-16 GB machine, 30,000-page PDF, network physically disabled, Corporate UI through FlaUI |
| PKG-MSI Windows MSI install/upgrade/uninstall | YES | YES | YES | N/A | N/A | NOT RUN | NO | docs/RELEASE_CANDIDATE_4.1.0.md. Build on 4.1.0 beta build only |
| PKG-APK Android companion APK | YES | YES | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-03. Build on 4.1.0 beta build only |
| PERF-001 TTFT, tokens/s, VRAM, startup, FPS | PARTIAL | PARTIAL | YES | N/A | N/A | BLOCKED-HW | NO | QA-023, docs/PERFORMANCE_BENCHMARKS.md |
| PERF-002 Idle Core memory runaway | NO | NO | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | docs/PERFORMANCE_BENCHMARKS.md (defect 1) |
| PERF-003 Same-profile reuse, routing, GameStop restore | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | docs/PERFORMANCE_BENCHMARKS.md. Same-profile warm reuse fixed; routing and GameStop parts remain open |
| SEC-001 Security audit gate | PARTIAL | PARTIAL | NOT RUN | NOT RUN | N/A | N/A | NO | QA-017, docs/SECURITY_REVIEW.md, independent static review of portable/Corporate diff (0 Critical, 2 High fixed in 9973463d, Medium open) |
| SEC-F01 Runtime ports without API key | NO | NO | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | docs/SECURITY_REVIEW.md (Not fixed) |
| SEC-F02 Unhashed managed Python installs and downloads | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (no remediation recorded) |
| SEC-F03 Backup restore applies security settings | YES | YES | YES | YES | N/A | N/A | NO | docs/SECURITY_REVIEW.md (Fixed), QA-021, QA-024 |
| SEC-F04 Paired device Internet/task authority | PARTIAL | PARTIAL | YES | YES | NOT RUN | N/A | NO | docs/SECURITY_REVIEW.md (Fixed, partial), QA-021 |
| SEC-F05 Browser session deny-list | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F06 Coding default permission bypass | YES | YES | YES | YES | NOT RUN | N/A | NO | docs/SECURITY_REVIEW.md (Fixed), QA-021 |
| SEC-F07 .NET 8 end of support | YES (retargeted to net10.0) | NO | Release build 0 errors; Server.Tests run recorded in docs/SECURITY_REVIEW.md | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md; packaging scripts not re-run on .NET 10 |
| SEC-F08a Secrets at rest (review finding, open Low) | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md review finding, open Low |
| SEC-F08b Corporate/Portable bootstrap token protection | YES | YES | YES | YES | N/A | NOT RUN | NO | EV-01, EV-02, EV-07 (High finding fixed in 9973463d) |
| SEC-F09 Pairing guessing and token expiry | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F10 Loopback Host-header allowlist | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F11 Token accepted from URL query | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F12 Runtime digest from same release API | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F13 Optional companion hashes and archive quota | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F14 Skill permission default-allow for unknown tools | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F15 Coding workspace root footgun | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F16 Torrent listener and gating | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F17 Self-signed LAN certificate rotation | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F18 Layer-3 egress proof not run | NO | NO | NOT RUN | NOT RUN | N/A | NOT RUN | NO | docs/SECURITY_REVIEW.md (open) |
| SEC-F19 Untracked evidence and .gitignore hygiene | YES (.ai/* ignored) | NO | tools/check-public-safety.ps1 OK | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (resolved) |
| SEC-F20 Tracked public identity data | NO | NO | NOT RUN | NOT RUN | N/A | N/A | NO | docs/SECURITY_REVIEW.md (open, confirm intent) |
| UX-001 Scaling 100-200%, QHD/4K/TV, multi-monitor | PARTIAL | PARTIAL | YES | N/A | PARTIAL | BLOCKED-HW | NO | docs/RELEASE_READINESS.md (gate 2) |
| BIC-01 Ledger state beyond 1,000 events | YES | YES | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (plan status DONE only) |
| BIC-02 Immutable benchmark start, edit-scope audit | YES | YES | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (plan status DONE only) |
| BIC-03 Independent evaluation and winner rules | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| BIC-04 Unified GPU/backend inventory, shared-memory budgets | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| BIC-05 Ollama request/response compatibility | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| BIC-06 Workspace navigation under WinUI layouts | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| BIC-07 Commit-linked release artifacts, signed updates | PARTIAL | PARTIAL | NOT RUN | NOT RUN | N/A | N/A | NO | none |
| BIC-08 Honest ONNX capabilities, cancellation, warm recovery | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| BIC-09 Coding orchestration and native editor review | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| BIC-10 Incremental semantic indexing | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| BIC-11 Tokenizer-bound cache identity | PARTIAL | PARTIAL | NOT RUN | YES | NOT RUN | N/A | NO | ManagedTokenizerIdentityTests (plan 2026-10-01 entry) |
| BIC-12 Native workbook formulas, charts, validation | YES | YES | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (plan status DONE only) |
| BIC-13 Media, training, voice depth and latency | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| BIC-14 Browser/mobile workflow and paired-client acceptance | YES | YES | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (plan status DONE only) |
| BIC-15 Isolated backup pools, strict local, listener races | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| BIC-16 Autonomous model discovery and Simple mode | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| BIC-17 Universal Local AI Gateway and one-click integrations | YES | YES | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (plan status DONE only) |
| RAG-01 Reranking in primary index + spreadsheet headers | YES | YES | YES | YES | N/A | NOT RUN | NO | EV-08 |
| L10N-01 No mixed Italian/English UI | PARTIAL | PARTIAL | YES | YES | NOT RUN | N/A | NO | EV-08. About 400 candidate strings open; Android has no Italian resources |
| APP-EXIT PaRuX.exe --exit | YES | YES | YES | YES | NOT RUN | N/A | NO | EV-08 |
| GOS-00 Baseline, audit, migration | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-01 Rebranding and assets | YES | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none (status line reads "WINDOWS VERIFIED NOT RUN") |
| GOS-02 Design system | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-03 Desktop UI/UX refresh | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-04 Console shell | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| GOS-05 Input hub | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| GOS-06 Game library and store | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-07 Download and install manager | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-08 Display and streaming | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | BLOCKED-HW | NO | none |
| GOS-09 Voice home and resource lifecycle | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none |
| GOS-10 QA, performance, security, release | NO | NO | NOT RUN | NOT RUN | NOT RUN | NOT RUN | NO | none (status NOT STARTED) |
| GOS-11 Universal library | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-12 Custom games, portable and local installers | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-13 Download manager (HTTP, embedded torrent WIP) | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| GOS-14 Emulator manager (backend) | PARTIAL | PARTIAL | NOT RUN | NOT RUN | NOT RUN | N/A | NO | none |
| 2026-11-10 Corporate Lite dependency and readiness tranche | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02, EV-06, EV-07 |
| 2026-11-10 Standard and Corporate Portable literal portability | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02, EV-05, EV-06, EV-07 |
| 2026-11-10 Gaming first run and Corporate token security | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02, EV-07 |
| 2026-11-08 Gallery video-thumbnail crash recovery | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-08 Gallery video-frame thumbnails | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-08 Gallery incomplete-output recovery and Create preview | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-08 Gallery ownership-safe deletion | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-07 Consumer Create/Gallery on-demand previews | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-07 First-run auto setup EN/IT recovery | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-07 Local AI model-start error recovery | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-07 Model Hub safe defaults and manual GGUF | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-07 Runtime-authoritative quant advice and controller binding | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-07 Model discovery and large-gallery concurrency | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-07 Create, Gallery and Android jobs usability | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02, EV-03 |
| 2026-11-07 Chat history and Group Chat safety | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-07 Standard/Portable UX and competitor comparison | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-07 Corporate over 1,000 sources and folder management | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Corporate generalist source scope and low-memory UX | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 General-purpose Corporate UX and edition consistency | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Corporate technical assistant and 30,000-page PDF | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 AI and Gaming parity review and active queue | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Training tranche 3 (KTO, completion-only SFT) | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-06 Training tranche 2 (16-bit LoRA, ORPO) | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-06 Quick Training LoRA/QLoRA | PARTIAL | PARTIAL | YES | YES | NOT RUN | NOT RUN | NO | EV-01, EV-02 |
| 2026-11-06 Real profile gaps and Corporate CPU readiness | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 UX and runtime gap closure after setup-profile shell | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Setup-aware navigation and Chat-first Corporate UX | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Corporate dynamic GGUF and preloaded CPU runtime | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 UX recovery phase 1 (onboarding, Model Hub) | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |
| 2026-11-06 Portable Standard regression and essential packaging | PARTIAL | PARTIAL | YES | YES | NOT RUN | BLOCKED-HW | NO | EV-01, EV-02 |

| SEC-M1 llama.cpp CPU archive digest is trust-on-first-use | YES | YES | YES | YES | N/A | N/A | NO | Fixed in `bdc190d4`: `packaging/llama-cpu-archive.lock.json` pins tag -> archive -> SHA-256 (b12282, b12283, both equal to the upstream release digest); an unpinned tag fails the build. Corporate package rebuilt with the pin (QA-042); Standard Portable builder not re-run |
| SEC-M2 Onboarding "starter included" check ignores the Core's bundled-model decision | YES | YES | YES | YES | NOT RUN | N/A | NO | Fixed in `bdc190d4`: the UI reads `BundledPortableStarterModel` from the Core. Not seen on screen |
| SEC-M3 CPU fallback after a failed GPU launch has no RAM admission | YES | YES | YES | YES | N/A | NOT RUN | NO | Fixed in `bdc190d4` (`CpuFallbackAdmission`); refusal text is English only. Not exercised on a low-RAM PC |
| ROUTE-KW Keyword heuristics outside document mode ("access code" read as coding; skill triggers by substring) | PARTIAL | PARTIAL | YES | YES | NOT RUN | N/A | NO | Document mode (c4b0560b, f87a2c16) plus plain chat: whole-word role signals and skill triggers (RouteKeywordBoundaryTests); App-side RouterService not audited |
| PKG-STORE Microsoft Store MSIX package | YES | YES | YES | YES | NOT RUN | PARTIAL | NO | QA-041, QA-043 (installed and started with package identity, Store rules refused through the Core, clean uninstall), QA-044 (local certification kit overall PASS, 2 optional failures). Open: window/UI pass and chat in the packaged app, real screenshots, reserved display name in the identity file, Partner Center certification |
| RAG-03 30,000-page PDF: streamed indexing, page citations | YES | YES | YES | YES | N/A | PARTIAL | NO | QA-037 (synthetic text PDF on the reference PC). OCR, resume after cancel, disk-full and low-RAM PCs not covered |
| PERF-CPU CPU engine memory (`--no-repack`) | YES | YES | YES | YES | N/A | PARTIAL | NO | QA-039, QA-042 (packaged Corporate peak 6.4 -> 4.65 GB); one CPU model measured, no 8 GB PC |
| RAG-02 Context sized to the model window; relevance filter; named-subject abstention; row-level lookup | YES | YES | YES | YES | N/A | PARTIAL | NO | 125e0ab3, 7772c22f, aaf09c8e; EV-09. Margin and 2.5 chars/token are first settings, not tuned on a large corpus |

## Gate status

| Gate | Status | Reason |
|---|---|---|
| A Git | PENDING | main pushed, no open PRs; branch feature/parux-ai-gaming-os-foundation has 5 older commits not yet evaluated |
| B Build | PASSED | Release x64 solution build 0 errors at 206440a9 (EV-01) |
| C Functional | PENDING | Most functional rows remain PARTIAL or NOT RUN in UI on current source |
| D AI | PENDING | Real CPU inference, grounded document chat and abstention verified in the Corporate package (EV-09); GPU chat, agents, model download and Standard Portable chat not re-run on current source |
| E Corporate | PENDING | Strict synthetic-corpus gate PASSED on the reference PC at aaf09c8e (EV-09); clean-PC, 8-16 GB, large-PDF and physically offline runs still pending |
| F Gaming | PENDING | Gaming rows are PARTIAL or NOT RUN; no UI or hardware run on current source |
| G UI/UX | PENDING | FlaUI regression 310/311 at 12028a3c with one test fix re-run alone (QA-035); scaling/4K/TV/controller passes and the visual audit of every screen not done |
| H Performance | PENDING | PERF-001 PARTIAL; PERF-002 and PERF-003 open; hardware benchmarks BLOCKED-HW |
| I QA | PENDING | Server xUnit 13,160/0 (QA-034), Android 41/41 (EV-03), FlaUI 310/311 + fix (QA-035), Corporate strict gate PASS (QA-032); Standard Portable chat/RAG, MSI, APK and hardware suites not run |
| J Packaging | PENDING | Standard (QA-036) and Corporate (QA-032) ZIPs pass their real acceptance on the build PC; MSI install/upgrade/uninstall and APK not re-run on current source; clean-PC verification and signing of portable EXEs pending |
| K Security | PENDING | 2 High findings fixed (EV-07); Medium findings open; many SEC-F rows still open |
| L Release | PENDING | Release Ready is YES only for REL-001, REL-002, REL-003 |
