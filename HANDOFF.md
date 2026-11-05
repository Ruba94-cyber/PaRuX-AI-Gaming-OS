## 2026-11-05 — Local game guide question control-character admission (SOURCE WIP)

- A user-supplied local guide question may be multiline, but NUL and other non-text control characters now fail closed at the owner HTTP boundary before index reads and in the reusable L8 policy. Synthetic HTTP and policy regression cases added. No local model, game, or network side effects. Compilation and Windows tests still pending.

## 2026-11-05 — Local guide hard-off before metadata reads, no draft-navigation surprise (SOURCE WIP)

- Games Home now refuses folder-list reads while Gaming is active, and explains when only the first 64 enabled guide folders are visible. Chat refuses occupied/unsent composer before navigation; exact nonempty-source state is unit-tested via a source-linked class (two new xUnit cases). The focused GOS Windows filter includes these tests. No build or test was executed in the remote session.

## 2026-11-05 — Local guide full UI handoff review and focused Windows gates (SOURCE WIP)

- Guide API validates consent/question first, avoids index reads on invalid input, and distinguishes invalid scope from unavailable local metadata; Chat composer now permits ordinary multiline question text while still rejecting NUL and preserving existing drafts. Navigation happens on user choice; Documents toggle visual reflects exact selected sources. Added synthetic FlaUI open/cancel smoke; GOS focused xUnit filter now includes LocalGameChatPolicyTests and LocalGameGuideEndpointTests. NOT RUN on Windows.

## 2026-11-05 — L8 local guide consent UI and existing Chat draft handoff (SOURCE WIP)

- Gaming Home adds an IT/EN local guide dialog: choose a saved manual game, enter a question, explicitly check source folders (none never means all), then Core re-verifies enabled and indexed IDs. A successful result is forwarded through UniversalLibraryPanel -> ManualGameLibraryView -> MainWindow to the **existing** Chat composer, not a new agent. Existing nonempty draft/busy Chat is preserved and the handoff fails; selected document IDs are atomically set only on accepted draft. Sending the question requires the user's next explicit click. No AI wakes during Gaming.
- Source-only wiring; WinUI build/FlaUI, real RAG index, race behavior, source-list scalability and GPU regression remain unverified. Game identification currently uses manual-library entry, not OS-attested live process.

## 2026-11-05 — L8 owner-only local game document Chat preparation (SOURCE WIP)

- `POST /api/v1/gaming/assistant/prepare` now binds the selected manual-library game ID to a real owner-registered game, explicit question and 1–64 currently enabled AND nonempty indexed source IDs. It rejects an empty scope (never fallback to every enabled folder), stale/unindexed IDs, Gaming/transition state, and unapproved clients. Returns only a bounded typed question and existing Chat document scope for a separate user-initiated Chat turn; no game binaries, AI inference, GPU allocation or network access. Synthetic policy/HTTP admission tests authored, NOT RUN.
- UI source picker, actual Chat invocation, process-attested current-game identity, Windows compilation/FlaUI and GPU isolation remain PARTIAL/OPEN.

## 2026-11-05 — Legacy native process test capture recovered (SOURCE WIP)

- Restored stdout/stderr separate native process collection and exit code preservation for .NET xUnit and Node browser gates, and Windows native/PowerShell failure sentinels via `-SelfTestNativeProcessCapture`. Old test runner from the feature branch was adapted to main's newer filters; other optional gates remain as before. Not yet run on Windows PowerShell/pwsh.

## 2026-11-05 — Recovered legacy Computer Use fail-closed checks (MAIN SOURCE WIP)

- Compared all ten surviving non-main branches to main, including old PR branch trees. All six PRs are merged and all source paths exist on main; the sole legacy-only path is an internal `.ai/telemetry` log, intentionally NOT imported. Selectively recovered from the historical foundation branch three still-applicable security checks: reject malformed UIA windows anywhere in the observation, reject null cross-window final readbacks, and re-check the live selected-app lease and allowlist during single-window final proof. Added synthetic xUnit refusal scenarios. No blanket merge/overwrite of newer code.
- Windows compilation, focused UIA/lease/Computer Use tests and real cross-window operation remain NOT RUN. Additional legacy script differences are still under audit. No release or branch deletion.

## 2026-11-05 — Audit of surviving GitHub branches and CLI help gap (SOURCE WIP)

- Found 11 remote branches including main; no open PR; PRs #1–#6 are already merged. Most old branches are behind main; the divergent `feature/parux-ai-gaming-os-foundation` includes an actual missing CLI agent help change. Ported only the isolated help routing and synthetic child-process regression test to main, rather than merging 61 stale files or bringing `.ai/telemetry` into main. `parux agent [--help|-h|help]` and `parux agent run [--help|-h]` now show agent guidance without starting Core or tasks; help inside an actual goal remains unmodified. No branch has been deleted.
- Legacy feature and remote branches remain retained until a separate explicit archive/delete decision. Old PR branch divergence after squash merges is NOT evidence by itself of missing functionality. Build and tests are still pending Windows validation.

## 2026-11-05 — Corrected GOS FlaUI Debug test build gate (SOURCE WIP)

- `tools/test-gos-checkpoint.ps1 -RunUi` retains its Release build verification, then builds a matching Debug Server+App pair, stages Debug Core beside Debug App and runs FlaUI Debug. Prior source ran Release FlaUI while fixtures require DEBUG; this could give false negatives. No actual Windows runner used here; revalidate the script on an isolated developer desktop.

## 2026-11-05 — Defense-in-depth imported title launch gate and Windows regression selection (SOURCE WIP)

- Third-party metadata providers cannot become launchable even if a stale/misleading payload marks one installed or unknown. Synthetic policies test all three providers. The local GOS focused xUnit filter now covers the external import store and HTTP routes, with Gaming Home already in optional FlaUI. Windows execution pending.

## 2026-11-05 — External library IT/EN owner import and metadata manager (MAIN SOURCE WIP)

- Gaming Home now offers a second explicit Save titles only action AFTER preview; saved entries are individually removable with separate confirmation. Source filters and labels distinguish uninstalled metadata-only titles. Typed owner client methods added. Windows UI compilation/FlaUI/13-inch/controller testing pending.

## 2026-11-05 — External library title-only opt-in import (MAIN SOURCE WIP)

- Added `ExternalGameLibraryStore` and explicit desktop-owner import/list/remove API for Playnite/Heroic/Lutris JSON. Import saves only validated provider, title and external ID (never export launch paths, arguments or credentials); duplicate IDs do not silently replace names. 1000 saved entries maximum, bounded versioned file, cross-process lease, safe non-link storage, atomic replace; Gaming Mode disallows import/removal. Preview stays read-only.
- Imported items participate in the unified Games catalogue as `metadata-only`, which is NOT launchable by the existing `GameTitleLaunchPolicy`; existing Steam/manual titles can still be launched through their separate verified installation.
- This is source-only and still requires compile, focused xUnit/HTTP, FlaUI, live 13-inch UI, and real Playnite/Heroic/Lutris export compatibility. Do not publish release 3.1.0.

## 2026-11-05 — Scoped Windows gaming downloads/mods gate expanded (SOURCE WIP)

- `tools/test-gaming-downloads-mods.ps1` now selects native torrent encrypted-restart, generic AI asset ZIP, opt-in ZIP drop-in and selected-game process guard xUnit regressions in addition to the existing download/portable/config tests. This is test gate wiring only, NOT a Windows execution result.

## 2026-11-05 — Gaming hard-off after torrent bandwidth wait (MAIN SOURCE WIP)

- Native single-file copy and multi-file ZIP packaging now recheck cancellation + Gaming Mode immediately after each throttling wait and before every payload write, with another check before the final flush. Synthetic tests simulate Gaming activating *inside* the limiter callback; neither path may publish the transfer. No Windows compile, xUnit execution, or peer fixture has been run. Remaining race window is non-atomic and requires Windows validation.

## 2026-11-05 — Native torrent length gate + orphaned mod receipt cleanup (MAIN SOURCE WIP)

- Native torrent copying now checks both individual and total v1 payload lengths **before writes**, and rejects a shortened single-file stage at EOF, rather than treating any truncated copy as a finished download. Shared bounded byte accounting also protects multifile ZIP packaging from overrun before archive writes. New synthetic accounting regression is authored, **NOT RUN**.
- If the owner has already deleted a PaRuX ZIP and the game EXE has disappeared, opt-in removal can clear only the orphaned PaRuX receipt, preserving all remaining game files; dangling ZIP links fail closed. Existing/modified ZIPs still require selected-game process attestation and matching SHA-256. New synthetic recovery test is authored, **NOT RUN**.
- No Windows .NET SDK, xUnit, FlaUI or peer swarm is available in this remote session. Run focused `EmbeddedTorrentTransportTests|EmbeddedMultifileTorrentTests|GameModDropInServiceTests`, full Server and FlaUI gates, and a lawful local torrent fixture. The changes do not assert peer/protocol interoperability or universal game mod compatibility. Do not release/tag 3.1.0.

## 2026-11-05 — Selected-game process attestation for optional mod installs (MAIN SOURCE WIP)

- New `GameModProcessGuard` validates the owner-selected local, regular, non-junction `.exe` and conservative Windows process identity via `Process.GetProcessesByName` + `MainModule.FileName`; same-named inaccessible processes **fail closed**, not interpreted as a closed game. System/security processes remain excluded using existing `GameAutoSuspendPolicy`. It DOES NOT prove that helper/launcher/child processes of another name are stopped.
- `GameModDropInInstallRequest` now REQUIRES `GameExecutablePath`. The mod service checks the selected EXE before processing and immediately before ZIP publish, persists the path with the SHA receipt, and checks it twice before removal (including if a game was merely suspended via Quick Resume). Legacy receipts with no executable remain visible as `unbound`, but cannot authorize deleting an existing ZIP. Orphan receipts for files already absent can be cleared without touching a game.
- `GameModDropInDialog` prompts for the actual EXE (not launcher) and exposes the bound identity in the installed-mod inventory. Synthetic xUnit tests validate running/paused blocks, a game relaunching between admission and publish/removal, invalid EXEs and old unbound receipts; new `GameModProcessGuardTests` covers path rules and Windows-only probing. Included in the local Windows GOS checkpoint gate.
- **Not compiled/tested here**: Windows .NET SDK/WinUI/FlaUI/PowerShell unavailable, all generated source remains WIP. The install/remove now re-attests the selected game EXE after potentially slow SHA verification, immediately before ZIP publish/deletion; synthetic xUnit also models a relaunch at that final gate. This is a conservative, repeated point-in-time observation, NOT atomic protection from a game relaunching precisely after the last check, or from alternate process names. Run Windows xUnit/FlaUI plus an eligible non-Minecraft game closed/running/Quick Resume-suspended test before acceptance; keep release 3.1.0 unpublished.

## 2026-11-05 — ZIP mod inventory budget + local Windows checkpoint coverage

- The installed ZIP mod inventory refuses to hash packages during Gaming Mode. When inspected from the desktop owner UI, the digest-check budget is bounded to **64 MiB per manual inventory read**; remaining present packages are labelled `unverified`, never falsely `installed`. SHA-checked removal of an individually selected package is still mandatory. The owner-only GET returns 409 when Gaming Mode is active.
- The Windows-only `tools/test-gos-checkpoint.ps1` GOS xUnit gate now explicitly includes `GameModDropInServiceTests`, `GameAssetModServiceTests`, `MinecraftSkinUvRestylerTests`, `GameSkinStudioServiceTests`, `TemporaryGameModServiceTests`, `NativeTorrentRecoveryStoreTests`, embedded torrent and owner hardening suites. Opt-in `-RunUi` includes `GamingHomeUiTests` and `QuickResumeAndMetadataUiTests`; Gaming Home FlaUI now exercises the cross-game asset editor's cancel path and read-only installed-mod inventory. **No runner was executed** on this remote host; no Windows .NET/PowerShell tools are present.
- Remaining acceptance: platform build, real Windows zip-file rename/uninstall races, target-folder permission/junction tests, actual legal non-Minecraft loader validation, semantic texture quality and executable/anti-cheat security review. Do not publish 3.1.0 or claim universal game mod functionality.

## 2026-11-05 — Cross-game opt-in ZIP drop-in mod delivery (MAIN SOURCE WIP)

- New `GameModDropInService` and `GameModDropInContracts`: ONLY a PaRuX-exported SHA-verified 2-entry PNG overlay + PaRuX manifest ZIP is eligible. Exact owner-selected existing local `mods`, `resourcepacks` or `addons` folder, explicit confirmation that the specific loader accepts standalone ZIP and that the offline game is fully closed, refusal during Gaming Mode. No archive extraction, installers, scripts, executable changes, DRM modification or in-place overwrite; creates `parux-mod-<guid>.zip` using create-new/rename. A durable PaRuX-owned receipt is written BEFORE the copy. `List` reports installed/missing/changed/unsafe; explicit `Remove` deletes only if ZIP SHA still equals the recorded hash and retains modified files/receipts, including following Core restart. Orphan receipt can be cleared when the mod folder is gone.
- Added desktop-owner `/api/v1/gaming/mods/drop-in` GET, `/install` POST, `/remove` POST, Core DI, typed client, IT/EN `GameModDropInDialog` and Gaming Home `Installed AI mods` button. After a successful texture export, an optional second dialog offers compatible-folder installation without silently writing game files. Capability list includes `zip-drop-in-owner-confirmed` but explicitly says there is no universal in-game loader compatibility or hot reload.
- Synthetic `GameModDropInServiceTests` source covers consent, isolation, original mod preservation, duplicate prevention, stale/modified ZIP rejection, restart and hash-guarded removal, missing-folder cleanup and hostile archive rejection. Browser owner route and FlaUI Games Home button assertions updated. **NONE EXECUTED on Windows.** This is an optional deployment ADAPTER for games with documented compatible ZIP drop-in handling, NOT a general mod installer; semantic game-specific loader integration remains MISSING.
- Windows gate: `tools/check-public-safety.ps1`, `dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64`, `dotnet build src/PaRuX.App -p:Platform=x64`; xUnit filter `FullyQualifiedName~GameModDropInServiceTests|FullyQualifiedName~GameAssetModServiceTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests`; full Server suite and solo FlaUI `GamingHomeUiTests`, installation/restart/uninstallation with compatible non-Minecraft mod loader, antivirus/reparse/race and 13-inch IT/EN UI check. No release/tag 3.1.0 from cloud.

## 2026-11-05 — Cross-game AI Asset Mods, Minecraft is only an example (MAIN SOURCE WIP)

- User explicitly clarified Minecraft was illustrative, not the focus. Created `GameAssetModService` (local model + imported PNG + safe relative asset path, bounded previews, SHA fingerprints and explicit export-only ZIP), `GameAssetModContracts`, `GameAssetModEndpoints` (owner-only), registered in Core and typed `GamingLibraryClient` methods. A new IT/EN `GameAssetModStudioDialog` is launched directly from Games Home via `GameHome_AssetStudio_Open`; no Quick Resume pause required for safe offline ZIP preview/export. `GameAssetModService.Adapters()` exposes capability truth across generic PNG, Minecraft-specific UV skin and paused managed config edits. `PngAssetGuard` is shared, not Minecraft-specific. No automatic overwrite or install and no generic promise of live game updates.
- Added `GameAssetModServiceTests`, browser desktop-owner hardening cases and FlaUI Gaming Home button accessibility source. **NONE WERE RUN** in this remote cloud session. ZIP packaging, GDI+, UI picker, dimensions, AMD/NVIDIA local model, legal game asset loader and data-loss recovery must be verified with Windows x64.
- Windows source gate: run `tools/check-public-safety.ps1`; `dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64`; `dotnet build src/PaRuX.App -p:Platform=x64`; `dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~GameAssetModServiceTests|FullyQualifiedName~MinecraftSkinUvRestylerTests|FullyQualifiedName~GameSkinStudioServiceTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests|FullyQualifiedName~TemporaryGameModServiceTests"`; full Server xUnit and solo FlaUI Regression including `GamingHomeUiTests`. Confirm ZIP contains only one expected texture and manifest with a real *non-Minecraft* mod loader; otherwise no compatibility claim.
- Next: versioned opt-in per-game adapter/plugin interface, guaranteed manual-install/rollback where title's supported API exists, DDS/TGA/model/animation content support, actual external mod-loader interaction, explicit while-paused workflow without violating gaming GPU hard-off. Keep release 3.1.0 unpublished until Windows acceptance.

## 2026-11-05 — Pinned MonoTorrent 3.0.2 source-level API correction (MAIN SOURCE WIP)

- Audited published upstream `release-v3.0.2` `ClientEngine`/`EngineSettings`/`EngineSettingsBuilder`. Found three compile-time API mismatches in `EmbeddedTorrentTransport`: direct read-only `EngineSettings` initialiser, nonexistent `MaximumDownloadSpeed/MaximumUploadSpeed` properties and record `with` syntax, plus `DownloadMetadataAsync` returning `ReadOnlyMemory<byte>` rather than `byte[]`. Corrected to `EngineSettingsBuilder.ToSettings()`, `MaximumDownloadRate/MaximumUploadRate`, builder-based `UpdateSettingsAsync` and explicit `.ToArray()` for metadata. Also inspected pinned `Torrent.Load(ReadOnlySpan<byte>)`, `ClientEngine.AddAsync(Torrent,...)`, `TorrentManager.StopAsync(TimeSpan)`. Do not assume this is compiled or peer-tested; run the Windows gate before declaring operational.
- Re-run focused tests `EmbeddedTorrentTransportTests|EmbeddedMultifileTorrentTests|NativeTorrentRecoveryStoreTests|GameDownloadManagerTests` and a lawful synthetic peer fixture with magnet metadata, stage resume, no seeding post-publish and Gaming cancellation. No GitHub Actions/release.

## 2026-11-05 — UV-preserving local AI skin edits (MAIN SOURCE WIP)

- Added `MinecraftSkinUvRestyler.cs` with official Java 64×64 Classic/Slim UV box positions, targeted `head/torso/arms/legs/full` face mapping including overlays, and original RGBA preservation outside the selection. Locally generated material imagery supplies a colour/texture reference. Existing skin bytes stay untouched; the result is a temporary reviewed draft and owner-approved **new PNG export**, not an in-place game/account change. Input is restricted to PNG with safe IHDR bounds before native decoder.
- Wired `GameSkinRestyleRequest`, owner-only `/api/v1/gaming/mods/skin-studio/restyle`, typed desktop client, IT/EN imported PNG picker + body-part choice. Retained experimental from-scratch generation. WinRT preview stream lifetime hardened.
- Added synthetic `MinecraftSkinUvRestylerTests`, browser-owner route cases and FlaUI Quick Resume button assertion. **No tests/build run in this remote session.** New generation still cannot be called during Gaming and has no live game hot-reload, asset overlay, account skin import or complete character mesh editing.
- Windows gate: `dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64`; `dotnet build src/PaRuX.App -p:Platform=x64`; `dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~MinecraftSkinUvRestylerTests|FullyQualifiedName~GameSkinStudioServiceTests|FullyQualifiedName~NativeTorrentRecoveryStoreTests|FullyQualifiedName~EmbeddedTorrentTransportTests|FullyQualifiedName~GameDownloadManagerTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"`; full Server xUnit, then solo FlaUI Smoke + Regression, GPU/local media test, real Minecraft import and lawful BitTorrent peer swarm. Run `tools/check-public-safety.ps1` before any further publish and keep 3.1.0 unreleased until physical acceptance.

## 2026-11-05 continuation — Local AI game skins + encrypted native torrent restart (MAIN SOURCE WIP)

- **Game Skin Studio first slice:** `GameSkinStudioService` uses the existing PaRuX local `MediaGenerationService` (no cloud model) to generate a candidate Minecraft Java character UV sheet, validates/renders it to a 64x64 PNG and keeps the draft in memory for 10 minutes. Desktop-owner-only `/api/v1/gaming/mods/skin-studio/generate`, `/drafts` and `/export` endpoints enforce separate approval. `MinecraftSkinStudioDialog` is accessible in Quick Resume; it previews the actual PNG and exports a new PaRuX-owned PNG path. **Not a guarantee of Minecraft UV layout validity, character hot swap, direct game/account modification or integration with a game mod loader**. Image generation is blocked by Gaming Mode. New tests are SOURCE ONLY.
- **Native torrent restart:** `NativeTorrentRecoveryStore` DPAPI-encrypts pending integrated-native torrent source state for the same Windows user; raw magnet trackers/passkeys are not stored as plaintext or displayed. Restored jobs are **paused until the owner clicks Resume**, with auto-install switched OFF. Existing per-job GUID stage and v1 piece validation remain. No automatic reconnection on startup; external client and HTTP jobs not persisted in this store. ZIP-packaged multifile native v1 torrents are described accurately in the Downloads UI; raw folder deployment and generated ZIP auto-install remain unsupported.
- **Tests added but NOT RUN:** `GameSkinStudioServiceTests`, `NativeTorrentRecoveryStoreTests`; existing `EmbeddedTorrentTransportTests`, `EmbeddedMultifileTorrentTests`, `GameDownloadManagerTests`, `TemporaryGameModServiceTests`, Browser owner hardening and IT/EN FlaUI must all run on Windows. No NuGet restore or compilation occurred in this cloud session.
- **Windows gate:** `tools/check-public-safety.ps1`, `dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64`, `dotnet build src/PaRuX.App -p:Platform=x64`, targeted tests with filter `FullyQualifiedName~GameSkinStudioServiceTests|FullyQualifiedName~NativeTorrentRecoveryStoreTests|FullyQualifiedName~EmbeddedMultifileTorrentTests|FullyQualifiedName~EmbeddedTorrentTransportTests|FullyQualifiedName~GameDownloadManagerTests|FullyQualifiedName~TemporaryGameModServiceTests`; then full Server xUnit and solo FlaUI Smoke/Regression. Real local image GPU, Minecraft skin import and a lawful local peer swarm must be tested. No MSI/APK/tag/release 3.1.0 from cloud.

## 2026-11-05 follow-up — native torrent safety + auto-restoring temporary game mods (SOURCE WIP)

- Native BitTorrent: selected local .torrent sources are read once under a file-share lease, hash-checked from the bytes actually transferred, with duplicate critical keys refused. Pre-existing GUID-owned staging trees now fail closed on symlink/junctions before new peer writes, and cleaning remains bounded. Added synthetic source/stage tests.
- Temporary game mods: original text is automatically restored ONLY when the captured process exits, no newly launched matching game instance exists, and the file still equals the originally applied SHA. A 15-second lightweight recovery host is disabled during Gaming; it retains backups for conflicts and manual recovery. Added synthetic tests for live-process refusal, restart and conflict.
- Both changes are pushed to `main`, but NOT COMPILED / NOT TESTED on Windows; no claim of functioning peer download, portable install, or game mod hot-reload. No release/tag from cloud.

# PaRuX hand-off (written 2026-11-05) - continue on another PC

## 2026-11-05 continuation — torrent fixes and temporary AI game mods (SOURCE WIP)

- Native MonoTorrent stage cleanup is bounded, recursive only for PaRuX-owned GUID stage trees and refuses links. Core DI now explicitly constructs the manager with integrated native torrent and portable ZIP post-deployment services; internal C# type visibility corrected. Actual MonoTorrent 3.0.2 package API and peer transfer NOT COMPILED OR TESTED.
- New TemporaryGameModService allows owner-approved text-only (.ini/.cfg/.json/.txt) edits to OFFLINE portable games PaRuX manages while the exact Quick Resume process is paused. SHA-bound preview (5-min TTL), original backup journal, atomic replacement, conflict-safe explicit restore. A game may require restart to read changes; no automatic restore at game exit.
- The AI game_mod_propose tool requires a separate single-use owner confirmation, returns only an in-memory preview ID and cannot apply/restore. Quick Resume UI includes Review AI mods, AI Mod and Restore AI mods, with full before/after human review. Owner-only API GET/POST routes; no online, anti-cheat, memory hooks or EXE/DRM changes.
- New source-only TemporaryGameModServiceTests, agent consent, browser security and FlaUI checks added. All are AUTHORED-NOT-RUN, not proof of Windows integration.

Run on Windows main after reviewing newest sources and with no other PaRuX instance:

    powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
    dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64
    dotnet build src/PaRuX.App -p:Platform=x64
    dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~EmbeddedTorrentTransportTests|FullyQualifiedName~GameDownloadManagerTests|FullyQualifiedName~PortableGameInstallServiceTests|FullyQualifiedName~TemporaryGameModServiceTests|FullyQualifiedName~LibraryAgentToolsTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"
    dotnet test tests/PaRuX.Server.Tests
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Smoke
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression

Required real-world checks: legal local peer swarm and torrent metadata; closed/paused game config before/after review; explicit restore after exit; restart behavior and crash backup recovery; controller and 13-inch IT/EN UI. Multifile torrents and auto-rollback at game exit remain NOT IMPLEMENTED. Never publish 3.1.0 from cloud.

Truth order: code > tests > live evidence > PARUX_EXECUTION_PLAN.md. Repo is PUBLIC: never commit company/personal data; run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1` before every commit (maintainer deny-list is git-ignored, never print it).

## State of main (all pushed)
- Merged: Model Hub Start board, setup profiles (AI/Gaming/Both) + runtime transitions + role profiles ("Use for"), portable ZIP builder, Local Knowledge & Search (extractors, FTS5+trigram+RRF, incremental index, chat-with-documents with citations, OCR, Light profile, strict offline, sensitive paths, secure delete, /diff and /contradictions), Corporate Lite mode + `scripts/build-corporate-lite.ps1`, Gaming completion (saves, hooks, IGDB, home/filters, Quick Resume), quant-ladder policy, section L competitive plan.
- Tests: full Server xUnit 12410 passed / 0 failed / 9 skipped (run 2026-11-05). FlaUI Smoke 34/34; full UI suite 270/283 with the 11 failures all re-run green (24/24) after republishing the Core into `src/PaRuX.App/bin/x64/Debug/.../win-x64/PaRuX.Server` (stale bundled Core was the cause). Re-run the FULL UI suite once, alone, before release.
- NOT done / not verified: release 3.1.0 (no tag, no MSI/APK/ZIPs published; latest GitHub release is 3.0.0), controllers/stores/IGDB (Needs physical HW), real-app check of Setup transitions + Role profile panel + new Docs skills buttons + secure-delete button, Corporate Lite on 8 GB (unclaimed), Corporate Lite answer quality of the 4B model on the synthetic corpus, WindowsCodingSandbox test `Bare_program_names_do_not_resolve...` hung once in isolation (icacls; TEMP has ~26k dirs) but passed in the full run.

## Decisions taken
- Corporate Lite bundled model: Qwen3-4B Q4_K_M (Apache-2.0); Qwen3.5-4B UD-Q4_K_XL was slower (12.8 vs 17.8 tok/s) and larger. Script default is already the 4B. Embeddings all-MiniLM-L6-v2, llama.cpp CPU b12119 (script picks the newest release that has the asset; "latest" is a stub).
- Speculative decoding measured and rejected; inference flags unchanged (see plan XLVIII D).
- Never use the NuGet id UglyToad.PdfPig (look-alike); PdfPig 0.1.16 is the genuine one.

## Release runbook (3.1.0)
1. `git pull`, clean tree. Bump version like commit 9a8c168: `src/PaRuX.App/PaRuX.App.csproj` and `src/PaRuX.Server/PaRuX.Server.csproj` (Version/FileVersion/AssemblyVersion/InformationalVersion 3.1.0), `src/PaRuX.Android/app/build.gradle.kts` (versionCode 19, versionName 3.1.0), README beta line. CHANGELOG entry. Guard, commit, `git tag v3.1.0`, push main + tag.
2. Env (bash `powershell.exe`, never echo secrets): `PARUX_ACCEPT_WIX7_EULA=1` (owner authorised); `PARUX_SIGN_PFX` / `PARUX_SIGN_PFX_PASSWORD` from `Documents\FirmaApp-PaoloRubagotti` (pfx + password txt); `PARUX_RELEASE_PRIVATE_KEY` / `PARUX_RELEASE_KEY_PASSWORD` from `Documents\LocalAI` (manifest pem + password txt). Then `scripts/build-msi.ps1 -Release` (needs clean tree at HEAD), `scripts/test-msi.ps1` if time.
3. Portable: `scripts/build-portable.ps1` (-Smoke). Corporate Lite: `scripts/build-corporate-lite.ps1 -Fetch -Smoke` (assets cache in artifacts/corporate-lite/assets, gitignored; ~2 GB ZIP).
4. APK: from a clean checkout at the tag, `PARUX_ANDROID_KEYSTORE`=`Documents\LocalAI\PaRuX-android-release.keystore`, store/key passwords from the owner's files; `scripts/publish-android-apk.ps1 -Tag v3.1.0` (needs the GitHub release to exist first, or use -LocalOnly).
5. `scripts/verify-release-assets.ps1`, then `gh release create v3.1.0` with MSI, portable ZIP, Corporate Lite ZIP + sha256, APK, manifest; notes must keep honest labels (Implemented / Auto-tested / Verified / Needs physical HW).

## Process rules learned
- Never run UI/FlaUI suites concurrently (single instance + port 51123); build UI tests with `-p:Platform=x64`; background shell jobs: `cmd &` returns immediately, wait with an until-loop on a "done" line.
- Python bench scripts on Windows need absolute Windows paths.
- Agent branches (worktree-agent-*) are all merged into main; WIP of stopped agents was committed on their branches.

## 2026-11-05 continuation on main: manual Steam prices (WIP)
## 2026-11-05 continuation: automatic verified ZIP portable game deployment (SOURCE WIP)

- `GameDownloadRequest` has **default-OFF** `AutoInstallPortable`, `PortableExecutableRelativePath`, `PortableGameTitle`. Enqueue demands .zip output, independently obtained 64-hex SHA-256, valid safe relative game EXE and title, and confirmation in the IT/EN Games Downloads UI. No content distributor, torrent title or AI agent can opt in implicitly.
- `PortableGameInstallService` (Core DI registered) handles completed verified archives after HTTPS/magnet/.torrent: byte rehash and read-only share lease; bounded ZIP member inventory, duplicate/path/symlink detection; existing safe extractor to managed partial; same-volume directory rename; `ManualGameLibrary.Add` with the user-chosen game executable. It does NOT execute any installer or launch the game. Cancellation/Gaming and validation errors retain the original archive; managed temporary files are rolled back where safe.
- Distinct PostInstallState is available in download DTO/history/UI. Synthetic `PortableGameInstallServiceTests`, `GameDownloadManagerTests` (local HTTP ZIP full path + invalid ZIP) and UI opt-out assertions are authored, **NOT COMPILED OR RUN**. The downloaded archive is not equated to a trusted publisher merely because bytes match an expected SHA-256.
- `AutoInstallPortable` does **not** support 7z/RAR/ISO or split/multi-file torrent packages; no arbitrary setup EXE, silent MSI, scripts, EULA automation, DRM workarounds, source catalogues or unattended elevation. Need a separately risk-reviewed signed-provider installer adapter and transactional recovery design before offering those.

On developer Windows PC run after pulling main, with no other PaRuX/FlaUI instance:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~PortableGameInstallServiceTests|FullyQualifiedName~GameDownloadManagerTests|FullyQualifiedName~EmbeddedTorrentTransportTests|FullyQualifiedName~GameDownloadTorrentTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate-license-inventory.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Smoke
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression
```

## 2026-11-05 continuation on main: native torrent transfers (GOS-13, SOURCE WIP)

Owner asked for Hydra-like in-app torrent management without qBittorrent as a mandatory external program. Existing HTTPS queue plus an optional qBittorrent WebUI adapter were preserved. **Source implemented on `main`, NOT BUILT/TESTED on Windows:** `src/PaRuX.Server/Orchestration/Gaming/Downloads/EmbeddedTorrentTransport.cs` uses MIT `MonoTorrent 3.0.2` packaged in Core, limited to explicitly user-selected v1 single-file magnets/`.torrent`; per-job transport assignment and new DI registration; no implicit torrent catalogue or repack sources. Existing queue pause/cancel, Gaming hard-off, rehash before publish and optional external qBittorrent remain. UI Games Downloads now supports source kind + local .torrent picker, consent warning about peers seeing IP, accessible progress indicators and visible-activity-only refresh; client/status owner-only endpoint. Corporate Lite denies Gaming endpoints. `THIRD_PARTY_NOTICES.md`, README and canonical plan are updated.

**The most important blockers before release:** NuGet restore and actual MonoTorrent v3.0.2 compile/API check, offline synthetic metadata xUnit, complete single-file torrent transfer with local legal fixture swarm, abort/interrupt/resume without staging deletion, no port leak/seeding after completion, active Gaming suspension, speed cap, disk-full/corrupt/linked-file scenarios, mono torrent v1/hybrid handling, owner-only API, FlaUI IT/EN at 13-inch and controller. `GameDownloadTorrentTests` still tests the optional external client with a fake qBittorrent; `EmbeddedTorrentTransportTests` is source-authored but unrun. Multifile atomic folder publication and crash/restart persistence of user-provided sources remain **MISSING**.

Run on the development Windows PC (one FlaUI suite at a time; no GitHub Actions):

```powershell
git switch main
git pull --ff-only
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet restore src/PaRuX.App/PaRuX.App.csproj -p:Platform=x64
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~EmbeddedTorrentTransportTests|FullyQualifiedName~GameDownloadTorrentTests|FullyQualifiedName~GameDownloadManagerTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate-license-inventory.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression
```

Do not cut/sign/tag release 3.1.0 until the full Windows acceptance procedure passes. No source here was run in the cloud. No claim of parity with all Hydra torrent features, torrent v2/hybrid or multi-file game repacks.



- **Implemented in source on main, no build/tests run in this cloud session:** explicit manual POST `/api/v1/gaming/wishlist/{productId}/quote` requires desktop-owner authorization and `Consent=true`, the existing separately enabled Game Discover consent, a saved valid numeric Steam ID/country, and Gaming inactive. Only requests `https://store.steampowered.com/api/appdetails` with fixed parameters. Store `cc` is **requested**, not a verified Steam-account region; results only shown as an indicative quote, never automatically notified or persisted.
- Reuses `SteamStoreCatalogProvider` with bounded HTTPS-only no-redirect/no-cookie/timeout policy and shared per-host rate limiter; `GameDiscoverRuntime` now keys its `ConditionalWeakTable` by `IHostApplicationLifetime` so request DI scopes do not reset the limiter. Rechecks Game Discover/Gaming and the current wishlist record after fetching. App has a one-click bilingual quote button and an accessible status area. No polling, Steam account login, purchases or free-price assumptions.
- Synthetic tests added/extended in `GameDiscoverProviderTests`, `BrowserOwnerAndLanHardeningTests` and `GamingHomeCompetitiveUiTests`; **all authored, not run**. Real Steam endpoint pricing JSON is not guaranteed stable: it is undocumented. No Windows build or hardware acceptance claimed.

Windows integration commands (run with current `main`; no parallel FlaUI):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~GameDiscoverProviderTests|FullyQualifiedName~LocalWishlistStoreTests|FullyQualifiedName~LocalWishlistPolicyTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression
```

Verify the opt-out/no-network case, manual quote with/without Discover consent, malformed/missing/free/region-mismatched Steam responses, cancellation when Gaming starts, host-shared rate limit, and 13-inch dialog/controller. Run the full Server and solo UI suites before release 3.1.0. No release, tag or binary publication from this cloud session.

## 2026-11-05 continuation on main: L5 local Steam wishlist (WIP)

New source commits on `main`: local wishlist metadata store with cross-process lease and atomic replace, no implicit opt-in; protected GET/POST/PUT/DELETE endpoints, explicit consent gate, App contract/client, compact IT/EN Gaming Home dialog and synthetic tests. No store account connection, price fetch, polling, notifications, payment or automatic game install. New files: `src/PaRuX.Server/Orchestration/Gaming/LocalWishlistStore.cs`, `src/PaRuX.Contracts/LocalWishlistContracts.cs`, `src/PaRuX.App/Views/GamingWishlistDialog.cs`, `tests/PaRuX.Server.Tests/LocalWishlistStoreTests.cs`; updated Program, GamingLibraryClient, GamingHomeView, LocalWishlistPolicy and BrowserOwnerAndLanHardeningTests. All newly authored source/tests remain **NOT COMPILED / NOT RUN in this cloud session**. The old Windows baseline cannot validate them.

Windows follow-up after pulling `main`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~LocalWishlistStoreTests|FullyQualifiedName~LocalWishlistPolicyTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"
# Then run the full xUnit suite and FlaUI Smoke/Regression individually as documented below.
```

Physical UI checks: Home's `GameHome_Wishlist_Button` in IT and EN; add/duplicate/error/format handling; saved state after restart; explicit watch consent toggles with no network; delete and corrupt-store recovery messaging; 13-inch display and controller navigation. This does NOT establish live Steam price monitoring or 3.1.0 release acceptance.

## Cloud source-only handoff — 2026-11-05 (PRs #1–#5 merged, not Windows-validated)

The five feature PRs below were **squash-merged into `main` on 2026-11-05**, with unverified code clearly labeled WIP. Every newly authored test remains **Authored-not-run**, and new C#/PowerShell source remains **Authored-not-compiled / not executed** in this remote session. The 12,410-pass Windows baseline predates these merges and does not validate this merged state.

Main merge commits: #1 `4f23fe6398f6506e8c96728b8e186835228a9177`; #3 `8b8a2427a1709024a6ade3cce7d0ed0124044cad`; #4 `110ad282d131e376b917a10d6f656d563cab6814`; #2 `53ce00ff1b2b0a6fe6daeb6a65e0fcdf057d16f9`; #5 `f16f8e92bebdb516823f37afbe42e23786b75596`. PR #6 reconciles the documentation.

| Block | Origin branch / merged PR | Exact new files or changes | Not yet demonstrated |
|---|---|---|---|
| L2 | `remote/l2-coding-review-policy-20261105` / [#1](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/1) | `src/PaRuX.App/Services/CodingHunkReviewPlanner.cs`; `src/PaRuX.App/Services/CodingSelectiveHunkPreview.cs`; `tests/PaRuX.Server.Tests/CodingHunkReviewPlannerTests.cs`; xUnit project links both sources | Real preimage recheck/transaction executor and actual per-hunk UI integration |
| L3 | `remote/l3-chat-compare-media-presets-20261105` / [#2](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/2) | `src/PaRuX.Contracts/ModelArenaContracts.cs`; `src/PaRuX.Server/{QuickChatComparisonPolicy.cs,QuickChatComparisonPresenter.cs,ModelArenaEndpoints.cs}`; `src/PaRuX.App/Services/ModelArenaClient.cs`; `src/PaRuX.App/Views/{ChatView.xaml,ChatView.ModelRouting.cs,ChatView.SimpleMode.cs}`; focused Server+HTTP tests; `tests/PaRuX.UITests/{PageObjects/ChatSimpleAdvancedPage.cs,Tests/SmokeTests.cs}` | Windows Core/App compilation, two-model real response/cancel, FlaUI advanced visibility/dialog and GPU fit (**tests authored-not-run**) |
| L8 | `remote/l8-local-game-chat-resource-policy-20261105` / [#3](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/3) | `src/PaRuX.Server/Orchestration/Gaming/LocalGameChatPolicy.cs`; `tests/PaRuX.Server.Tests/LocalGameChatPolicyTests.cs` | Integration with Chat, gaming event checks and physical GPU isolation |
| L6/L7 | `remote/l6-l7-suspend-controller-planners-20261105` / [#4](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/4) | `src/PaRuX.Server/Orchestration/Gaming/{GameSuspendControllerPolicies.cs,GameSuspendDebouncePolicy.cs}`; `tests/PaRuX.Server.Tests/{GameSuspendControllerPoliciesTests.cs,GameSuspendDebouncePolicyTests.cs}` | Live focus/battery/thermal events, anti-cheat confidence, controller/driver tests (**Needs physical HW**) |
| L4/L5/L9 | `remote/l4-l5-external-library-prices-20261105` / [#5](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/5) | `src/PaRuX.Contracts/ExternalLibraryPreviewContracts.cs`; `src/PaRuX.Server/Orchestration/Gaming/{ExternalLibraryPricePolicies.cs,LocalWishlistPolicy.cs,GamingEndpoints.cs}`; `src/PaRuX.App/{Services/GamingLibraryClient.cs,Views/GamingHomeView.cs}`; `tests/PaRuX.Server.Tests/{ExternalLibraryPricePoliciesTests.cs,LocalWishlistPolicyTests.cs,ExternalLibraryPreviewEndpointTests.cs,BrowserOwnerAndLanHardeningTests.cs}`; two source-audit PowerShell scripts | Existing-vendor export samples, Windows build, owner HTTP, FlaUI dialog, controller UX, price API consent (**all new tests authored-not-run**) |
| Documentation | `remote/l-roadmap-handoff-20261105` / documentation PR #6 | L section of `PARUX_EXECUTION_PLAN.md`, `CHANGELOG.md`, `README.md`, this `HANDOFF.md` | Windows gates after any code merge; no change to release runbook |

### Windows validation commands (on integrated `main`; no parallel FlaUI)

Use a clean Windows checkout of **`main`**, not the old feature branches. Five PRs were merged without Windows gates; source integration does not establish compilation, runtime safety or passing tests.

```powershell
git switch main
git pull --ff-only
git diff --check b2c1cd8c5a086d59f89f79451f3ebcda7cb837d2..HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~CodingHunkReviewPlannerTests|FullyQualifiedName~QuickChatComparisonPolicyTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests|FullyQualifiedName~LocalGameChatPolicyTests|FullyQualifiedName~GameSuspendControllerPoliciesTests|FullyQualifiedName~GameSuspendDebouncePolicyTests|FullyQualifiedName~ExternalLibraryPricePoliciesTests|FullyQualifiedName~LocalWishlistPolicyTests|FullyQualifiedName~ExternalLibraryPreviewEndpointTests"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-direct-third-party-coverage.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-direct-third-party-coverage.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/audit-it-en-literal-pairs.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File tools/audit-it-en-literal-pairs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate-license-inventory.ps1
dotnet test tests/PaRuX.Server.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-sbom.ps1 -Scope windows -ProductVersion 3.0.0
# Run FlaUI alone, one suite at a time, using the matching current bundled Core:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Smoke
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression
```

Do not release until all build, focused test, public-safety, full regression and hardware gates pass. In the real app verify Advanced Chat comparison/owner auth/cancel, Game Home JSON preview, setup transitions, role profiles, document skills, secure deletion and GPU hard-off. Physically test game controller/HidHide/virtual-pad recovery, real anti-cheat exclusions and suspend sensors, and actual vendor exports; verify Corporate Lite on 8 GB hardware and 4B answer quality. No XOutput equivalence is claimed.

### Compile/runtime risks to inspect

- L2: strict parser rejects unreviewable zero-hunk file sections, unknown/out-of-order metadata, repeated file headers, binary/add/delete/rename/copy metadata, and more than 500 hunks; quoted/nonstandard diffs remain unsupported. In-memory SHA-256 preview now matches the parser's whitespace/control-character path restrictions but still cannot re-read target files, handle BOM/encodings or execute transactional changes. Auto-in-sandbox requires **both** independently established OS sandbox and verified confinement of every mutation tool; never provide guessed evidence. Windows build + focused xUnit + UI are outstanding.
- L3: Arena and PersonaService already exist. Merged source has owner-only Core POST/GET, typed App client, IT/EN statuses and a bounded 4,000-char prompt. Advanced Chat Compare UI shows local installed-model choices and an explicit side-by-side response/cancel dialog with manual (not background) refresh. Verify WinUI XAML, ContentDialog sizing on 13-inch displays, provider session/cancel, two-model serial resource usage, exact owner auth and FlaUI Smoke **on Windows**; none were executed. Media GPU-memory floors are suggestions only, not 8 GB support or fit proof.
- L8: `ChatDocumentScope.SourceIds` empty means all enabled sources. New planner requires selected IDs to be a subset of **separately verified local-index allowed source IDs**, otherwise `Ready=false`; caller is responsible for independently resolving and authorising that set. Unknown Gaming telemetry now blocks GPU admission. This is a presenter, not proof of runtime isolation or indexed-source authorization by itself.
- L6/L7: anti-cheat list is deliberately incomplete; unknown classification, protected process names and user exclusion entries block suspend proposals. The new 3/5/10-second continuity tracker requires caller-provided monotonic timestamps, verified PID/start-time stamps and samples no more than 2 seconds apart; it restarts after rule edits, PID changes, focus recovery, missing/old samples; there is **no sensor/event integration**, and controller curves/typed owner-scoped rollback steps are previews, not drivers or executable recovery. All are **Authored-not-compiled / Needs physical HW**.
- L4/L5/L9: export JSON shapes are conservative synthetic examples, not full vendor-format coverage. The owner-only `/api/v1/gaming/library/import-preview` endpoint and new bilingual Game Home popup accept user-pasted JSON only; they never persist games, process installers or open paths in exported data. Verify WinUI dialog behavior, owner authorization, Gaming hard-off and input size limits physically on Windows. Wishlist price eligibility requires matching store/product/currency/**country** and fresh data, with country from the actual API query. No API fetch, persisted wishlist, duplicate suppression, regional proof or actual notifications; the source-only i18n scan cannot validate XAML/dynamic translations.

**Release boundary:** `3.1.0` stays UNRELEASED. No tags, MSI, APK, portable ZIP or GitHub release creation is authorized in this cloud session. The original Windows-only release runbook above is unchanged; only use it after all gates pass and the maintainer performs authorized local signing.
