# PaRuX — competitor feature/evidence gap audit (2026-11-06)

> Source review against current `main` and suppliers' official documentation, not an executed binary comparison. Code > tests > real runtime > roadmap. **Source-present ≠ Windows-verified.** No claim of performance or functionality superiority without controlled acceptance. Status: `SOURCE` means code observed in repo, `PARTIAL` means incomplete or not validated; `MISSING` means no complete implementation observed; `HW GATE` needs Windows/controller/GPU verification.

## Competitor evidence (official, checked 2026-11-06)

- [Unsloth source](https://github.com/unslothai/unsloth), [docs](https://unsloth.ai/docs): SFT/RL/DPO/GRPO, FP8/full fine-tuning, many modalities and families, dataset recipes from CSV/PDF/DOCX, real-time loss/GPU, merged weights and GGUF.
- [LM Studio server settings](https://lmstudio.ai/docs/developer/core/server/settings): authenticated local/LAN OpenAI-compatible API, JIT loading and auto-unloading, MCP access controls; server feature existence does not prove PaRuX parity.
- [Locally Uncensored 3.0.9 / 2026-11-05 roadmap](https://github.com/PurpleDoubleD/locally-uncensored): all-in-one local chat/code/image/video, group chat, model fit hints, pose/style training beta, video previews, Android APK and beginner-friendly no-terminal setup. Real Windows installer/regression acceptance is a different gate from PaRuX code existence.
- [Sunshine](https://github.com/LizardByte/Sunshine) / [Moonlight](https://github.com/moonlight-stream/moonlight-qt): Windows HDR/codec streaming plus gamepad and touch support; PaRuX Display/Streaming Manager requires a real host/client/HDR controller regression, not an invented native streaming engine.
- [Playnite](https://playnite.link/): imported multi-store libraries, emulators, portable + fast library, couch/fullscreen gamepad, tags/plugins, playtime.
- [Heroic](https://heroicgameslauncher.com/faq): Epic/GOG/Amazon login and installations, updates, repair, move, imports, queue, cloud save sync.
- [Hydra changelog 2026-11-05](https://hydra.workwonders.app/changelog): native recomp projects, leaderboards, RPCS3/RetroArch cloud saves; [source](https://github.com/hydra-launcher/hydra) includes embedded BitTorrent. Do not copy copyright/DRM-distribution services.
- [OpenGamepadUI](https://github.com/ShadowBlip/OpenGamepadUI): gamepad-first launcher, per-game input mapping, configurable overlays, artwork, plugins; several Linux-specific functions not directly transferable to Windows.

## AI gaps vs evidence in repo

| Capability | PaRuX evidence on main | Status / next gate |
|---|---|---|
| Local chat, model hub, desktop/portable, CPU Corporate | `ModelHubView`, `LocalRuntimeService`, setup profile policies, CPU runtime and GGUF bootstrap | SOURCE; full Windows/CPU acceptance still needed |
| RAG/document extraction/citations, offline search | `KnowledgeService`, `RagStore`, `DocumentChat` | SOURCE; real corpus quality/indexing benchmark |
| Unsloth-style quick SFT/QLoRA/LoRA16/DPO/ORPO/KTO/GRPO | `TrainingLabService`, `TrainingRuntimeCatalog`, `TrainingRunPlanner`, `LifecycleLabView` | SOURCE; compile + native GPU/VRAM/quality/eval |
| Data Recipes from CSV/JSON/Parquet | `DatasetCurationService` reads/inspects/splits these inputs, but cannot directly create supervised/preference JSONL from mapped columns | **P0 SOURCE IMPLEMENTED / WINDOWS UNVERIFIED**: explicit SFT/DPO/KTO column-mapped recipe, bounded atomic managed JSONL, SHA256, source preservation, owner-only+Corporate/Gaming refusal, Lifecycle UI + tests; real parsing, parquet and GPU acceptance pending |
| PDF/DOCX synthetic recipe authoring | Generic document extractors exist; no end-to-end reviewed training dataset authoring | **P1 MISSING**: safe extraction, explicit reviewed prompt/answer pairs, no unattended hallucinated targets |
| Full model/FP8/embedding fine-tuning + GSPO | No verified integrated training path compared with Unsloth | **P1 MISSING**, hardware/runtime compatibility gates |
| Train metrics GPU / VRAM / charts & export merged model | `TrainingJobStatus.History`, Adapter Manager conversion; complete family coverage not proven | **P1 PARTIAL**: live metrics, test true merge + GGUF/FP8 against model families |
| Locally Uncensored-style no-terminal creation + coding + video preview | Existing media generation, coding workflows, AI models and onboarding exist; unified single-window usability and performance not acceptance-tested | **P1 UX/PERF**: beginner flow, model fit, privacy, permissions, failure recovery; compare to v3.0.9 |
| Local 4K HDR streaming and controller forwarding | Existing Display/Streaming Manager + Sunshine/Moonlight conceptual wiring | **P1 HW GATE**: real HDR/VRR, codec fallback, controller forwarding and full restoration of Windows display mode |
| JIT unload / remote API / MCP permissions | Local API and runtime manager exist; scoped review for exact LM Studio behaviors absent | **P1 AUDIT**: auth, idle resource release, JIT, per-request MCP, model selection |
| General model-family auto-configuration | Unsloth runner hardcoded q/k/v/o + optional MLP; pinned runtime | **P1 PARTIAL**: adaptive target inspection, save/restore, model-specific templates |
| Parallel source of truth and user safety | Single owned runtime, no GitHub Actions, Game AI hard-off, Corporate restrictions | SOURCE; integration QA + regression |

## Gaming gaps vs evidence in repo

| Capability | PaRuX evidence on main | Status / next gate |
|---|---|---|
| Multi-store titles, manual/emulator, cover/artwork/filters | `GameTitleCatalog`, providers, `GamingHomeView`, `GameTitleReadCache` | SOURCE; scaled import, startup/scroll/controller QA |
| Save archive/verify/recovery | `GameSaveBackupService`, `GameSaveRestore` with explicit restore/verified staging | SOURCE; real save formats, locked files, rollback/PC test |
| Download queue + torrent/magnet | Embedded MonoTorrent transport + optional qBittorrent per repo evidence | SOURCE; legal-source use, network/privacy/rehash/performance QA; no DRM bypass |
| Owned-store install/update/repair/move | Store discovery/launch and game package stage exist; not full Heroic account management | **P1 PARTIAL/MISSING**: provider-specific permissioned install, verify/repair/move without deleting user saves |
| Recent/playtime/favorites/collections | `GameTitleStore`, `GameTitleCatalog`, UI | SOURCE; **P0 SOURCE IMPLEMENTED / WINDOWS UNVERIFIED:** pure GameLibraryHealthPolicy, cached overview, read-only attention dialog + tests, no disk scan during Gaming |
| Full gamepad-first UI and remapping | `GamingHomeView`, `ControllerProfileStore`, console controls | **P1 PARTIAL**: real Xbox/DS/DualSense/Stadia tests, focus loops, 13-inch/TV |
| Emulators/recomps | `Emulators`, library; no verified first-party native recomp install adapter | **P2** licensed/user-owned ROM policy + safe opt-in provider |
| Achievements/leaderboards | Some playtime/history, no backed account/server achievement ecosystem observed | **P2** explicit opt-in and supported provider SDK, never invent achievements |
| Cloud save sync | local safe archives, not equivalent to Epic/GOG/Hydra Cloud cross-device | **P1 MISSING**: opt-in user-owned provider, conflict detection, restore + offline safety |
| Performance hard-off | `WorkloadOrchestrator`, Gaming mode cancellation, controller/display profiles | SOURCE; frame-time and GPU VRAM benchmark with/without PaRuX (<2% FPS regression target) |
| Temporary game AI asset mods | `GameAssetModService`, `TemporaryGameModService` | SOURCE; per-game supported formats, explicit dry-run, ownership and revert acceptance |

## Priority execution queue / definition of done

- **P0 AI-A (SOURCE IMPLEMENTED):** real column-mapped SFT/DPO/KTO from existing CSV/JSON/JSONL/Parquet parsers to 32 MiB bounded atomic JSONL, kept under PaRuXDataRoot; 64 MiB input and 20k row max; no guessed labels; error if >5% missing or labels malformed; UI selects output for Training Lab. Build/native training still pending.
- **P0 GOS-A (SOURCE IMPLEMENTED):** derived unfiltered visible-library statistics and attention dialog from cached GameTitleDto, explicitly counts launch *candidates* rather than validated binaries, no store rescan or writes while Gaming. Hidden titles are excluded from the returned default catalog and not claimed as counted. Native FlaUI/controller check pending.
- **P0 CROSS:** Windows build, targeted/full xUnit, exclusive FlaUI, GPU training + controller/playtime acceptance; no releases/signing until done.
- **P1 AI:** model family support + adaptive LoRA targets, FP8/full fine-tuning, dataset extraction DOCX/PDF, merged export, VRAM/metrics graphs; per-hardware evidence.
- **P1 Gaming:** install/repair/update/move with supported owned-account providers, cloud-save conflict flow, couch interaction/TV focus and gamepad, emulators.
- **P2:** full achievements/leaderboards, recompiled native games, advanced RL and multimodal, plugin marketplace; only after security, licensing and performance review.

**Acceptance policy:** Nothing labeled DONE until source, specific tests, native UI, failure/cancel/rollback, Gaming hard-off, security and real Windows hardware are verified. Release artifacts and signatures remain only on authorized developer PC. All integrations opt-in with user-owned legal sources and local-first defaults. Avoid shallow feature count comparisons.

### 2026-11-06 first implementation tranche — source evidence (NOT hardware-verified)

- AI: `src/PaRuX.Server/DatasetCurationService.TrainingRecipes.cs`, `PaRuX.Contracts/DatasetContracts.cs`, `Program.cs` training-recipe endpoint, `DatasetHubClient`, `LifecycleLabView`; `TrainingDatasetRecipeTests.cs`, FlaUI reachability. Existing CSV parser no longer yields on every input character (cooperatively yields per 4,096 chars). No network, model or training is run on recipe creation; still need source-size/Parquet stress tests, E2E training and correct 13-inch WinUI layout.
- Gaming: `src/PaRuX.Contracts/GameLibraryHealthPolicy.cs`, `GamingHomeView.cs`, `GameLibraryHealthPolicyTests.cs`, gaming-home FlaUI scenario. Uses already fetched title metadata only, not an installation verifier; no new provider account or disk access, no cloud save or game repair claimed.
- Run `dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~TrainingDatasetRecipeTests|FullyQualifiedName~GameLibraryHealthPolicyTests|FullyQualifiedName~DatasetHubTests"` then full xUnit, exclusive FlaUI and real desktop UX. Do not issue release/sign until verified.

### Follow-up: bounded full-file Quick fine-tuning and actionable Gaming library (SOURCE / WINDOWS UNVERIFIED)

The previous Quick Training restriction to the first 2,000 verified rows is **removed within explicit bounds**. Fast 2k preview still detects method, but the final owner-requested plan verifies every SFT or DPO JSONL row (<=256 MiB, <=200k nonblank records, <=1M characters/row), exposes exact record count and SHA-256, recomputes recommendations for all records, then requires a final local GPU consent. The API does not trust the UI alone: `TrainingJobRequest.ExpectedDatasetSha256` is compared to Core's actual bytes before durable queueing; the worker checks again before GPU allocation. Late corruption or tampering must refuse; Gaming hard-off cancels full scan. Authoring is SOURCE IMPLEMENTED; no runtime benchmark is asserted.

Gaming Home's cached attention dialog now offers a *Show these titles in Library* action and *Show all titles* recovery. The action uses `GameLibraryHealthSummary.AttentionTitles` already in memory and must not query a store or inspect a game. The labels distinguish launch candidate from actual installed license/executable validation. Gaming controller/UI acceptance remains pending.

**New full-scan test:** `tests/PaRuX.Server.Tests/QuickTrainingDatasetValidatorTests.cs`. These are authored, not executed. Quick Training still does not support arbitrary >256MiB/>200k record datasets (Advanced remains the explicit route), nor does full dataset validation prove Unsloth quality/performance parity.
