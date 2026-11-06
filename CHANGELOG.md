## 4.0.0 (release candidate, unsigned, unpublished)

- Major release: Gaming engine (library, torrents, mods/asset studio, suspend/controller policies), Local Knowledge & Search, Corporate Lite portable, Codex-class selective per-hunk coding review, run-time AI/Gaming/Both transitions.
- Verified on Windows 2026-11-06: Server xUnit (chunked), FlaUI Smoke 35/35 and Regression 283/283 (chunked), Android unit tests + lint, MSI/Portable/Corporate Lite/APK built. See docs/RELEASE_CANDIDATE_4.0.0.md for what is NOT verified.

### 2026-11-05 — Coding agent selective hunk transaction foundation (SOURCE WIP)

- Added `CodingSelectiveHunkApplyService`: a one-file, existing-UTF-8-text-only application path for exact approved hunk decisions. Before any mutation it checks explicit approval, the reviewed diff, source/candidate SHA-256, path containment, encoding and size.
- Reuses the existing journaled shadow transaction rather than a separate patch writer: per-project lease, SHA-256 Git-blob preimage, staged replacement, durable backups, final postimage and rollback that preserves unexpected human edits. No commit/push or automatic agent approval.
- Six synthetic filesystem regression sources plus the focused Windows test selection added. **Not compiled/tested in this remote session**. Coding UI per-hunk integration, task receipt admission, real Windows races and full verification are open; this is not L2 DONE or part of a released 3.1.0 build.

### 2026-11-05 — Manual Steam wishlist multi-price checks (SOURCE WIP)

- Added a separate, explicit **Check watched prices once** action in the Games wishlist: runs up to eight opted-in, valid Steam items sequentially through the existing owner-only, consented single-price API and shared rate limiter. Skipped items are reported, and currency/storefront-country/target comparisons remain indicative, not a verified Steam account region.
- Added cancellation of BOTH single and batch quote calls on wishlist dialog closure, blocked conflicting UI actions during a batch, refreshed batch candidates after a user removes an entry, and bounded-scroll dialog layout for small screens. Opening the wishlist never sends price requests. No periodic monitoring, account login, alerts or buying.
- Added pure policy and FlaUI smoke regression sources. Updated local GOS Windows test selection. **Source only; Windows restore/build/xUnit/FlaUI, live Steam and Gaming-race validation NOT RUN.** No v3.1.0 publication.

### 2026-11-05 — L8 local guide Chat handoff with explicit indexed sources (SOURCE WIP)

- Added desktop-owner guide preparation API: real saved manual-game identity, consent, 1–64 enabled and indexed local folders, no implicit all-folder fallback, Gaming hard-off and AI-ready recheck. Added Games Home IT/EN dialog, existing RAG folder picker, and a non-sending handoff to the existing Chat composer with source-scope selection and collision refusal.
- Recovered selective legacy-branch Computer Use UIA/final-lease security guards and native test process capture, without merging obsolete code or the internal telemetry file. Focused xUnit and FlaUI sources authored. **NOT COMPILED/TESTED on Windows; no real game/GPU/RAG runtime acceptance.**

### 2026-11-05 — Metadata-only library imports from Playnite, Heroic and Lutris (SOURCE WIP)

- Users can preview explicit JSON exports and separately opt to retain provider/title/external ID only, up to 1000 entries. Stored records use safe bounded atomic writes, a cross-process lock and individual owner-approved removal; the original vendor files and launcher commands are never touched or retained.
- Saved titles appear in the unified Games catalogue with a non-launchable metadata-only state, IT/EN labels and provider filters. An additional launch-policy guard blocks these providers even if stale input claims an installed state. New Core HTTP and synthetic store/FlaUI tests are authored but not executed.
- **Windows restore/build/FlaUI, real vendor-export compatibility and hardware acceptance remain unverified.** No v3.1.0 release.

### 2026-11-05 — Bind mod ZIP install/remove to verified game EXE (SOURCE WIP)

- Opt-in ZIP mod install now requires the actual local game executable in addition to the owner confirmation, and conservatively checks its Windows process identity before installing, again before publish, and again before SHA-checked removal. A paused/suspended process is still running and blocks. Invalid/linked/system executables and inaccessible same-name processes fail closed.
- The recovery receipt persists the chosen game EXE. Older receipts without a bound game process are visible but cannot authorize deleting an existing ZIP. Added game EXE picker, installed-mod identity display, relaunch-race and OS process probe synthetic regression sources.
- Does not attest every differently named helper process, online state, game engine, or actual mod loader compatibility. Windows build and live OS/process/FlaUI acceptance are NOT RUN.

### 2026-11-05 — Consent-gated ZIP mod folder deployment (SOURCE WIP)

- Opt-in game-independent ZIP mod delivery now supports only an existing folder selected by the owner and independently documented to accept this exact package layout. New-file-only atomic placement, SHA validation, pre-copy recovery receipt, installation inventory and SHA-checked manual removal with changed-file retention; disabled during Gaming and requires the game to be closed.
- Added owner-only endpoints and IT/EN UI in Gaming Home and after mod asset export, focused synthetic regression/security/FlaUI test sources. No game executable, original asset or pre-existing mod files are overwritten; no loaders/anticheat are altered, no universal live reload claim.
- Windows build, xUnit, FlaUI, crash races and actual compatible game-mod-loader testing **NOT RUN**. The feature remains partial/unshipped.

### 2026-11-05 — Cross-game AI asset mod foundation (MAIN SOURCE WIP)

- Added `GameAssetModService`, game-agnostic DTOs, public capability inventory, desktop-owner endpoints and a first-class IT/EN `AI Game Asset Mods` action in Gaming Home. User selects existing PNG texture and safe asset replacement path, local model produces a colour/material variant while retaining original dimensions and transparency, then preview/explicit approval produces a self-contained ZIP with exactly one replacement texture and a PaRuX manifest. No game files or game sessions are edited; game-specific mod loader support and installation remain external/manual.
- Extracted a shared PNG security guard independent of Minecraft. Added synthetic tests for original-file preservation, source/preview/export and ZIP boundaries, path attacks, Gaming hard-off, signed owner routes and FlaUI accessibility.
- Status: SOURCE WIP only. No Windows compilation, real local AI model run, game interoperability/hot reload, installation/rollback or full regression tests done. Minecraft remains an example adapter; actual architecture is cross-game.

### 2026-11-05 — MonoTorrent pinned-version compatibility (SOURCE WIP)

- Updated embedded transfer setup against upstream MonoTorrent `release-v3.0.2` API: immutable engine settings via `EngineSettingsBuilder`, rate properties named `MaximumDownloadRate/MaximumUploadRate`, builder-based runtime updates and `ReadOnlyMemory<byte>` metadata conversion. This addresses identified source-level compiler mismatches; **Windows build/real peer transfer still NOT RUN**.

### 2026-11-05 — UV-aware local character restyling and torrent restart test coverage (SOURCE WIP)

- Existing Minecraft 64×64 Java skin can now be imported and selectively recoloured by a local image-generated material reference; UV maps for Classic/Slim head, torso, arms, legs and outer layers stay in their known positions. User-selected parts only; nonselected pixels and original files untouched. A new desktop-owner restyle endpoint and IT/EN PNG picker/body-part selector connect preview and consent-gated export.
- PNG IHDR preflight rejects oversized/non-PNG inputs before native decoding; added synthetic UV/alpha, owner-route security, recovered-torrent-no-auto-peer and FlaUI accessibility regression sources. Native restart remains opt-in and DPAPI-encrypted.
- **NOT COMPILED/TESTED on Windows**; no claim of actual Minecraft skin activation, rich mod-loader compatibility, peer transfer success, installer safety or shippable release.

### 2026-11-05 — Local AI Minecraft skin drafts + encrypted native torrent restart (SOURCE WIP)

- **Game Asset Studio, first slice:** new local-media-backed Minecraft Java skin draft flow in Quick Resume UI. A bounded owner-only request generates a 64x64 PNG candidate using the existing local image model, previews real PNG pixels, requires a second consent and exports only a new PaRuX-owned file; official game skin import and UV checks remain manual. Cannot claim live skin switching, 3D model edits, general texture patching or fully valid UV generated by diffusion.
- **Torrent:** native v1 multi-file output is packaged as a validated, manually named ZIP; the UI no longer calls it single-file-only. Native pending jobs now write DPAPI-encrypted Windows-user recovery state, including private magnet trackers, and restart visibly **paused**, without automatic peer reconnection or auto-install. HTTP credentials and optional external client jobs are not persisted in this store.
- Synthetic skin and DPAPI regression tests added. **No .NET compile, NuGet restore, real torrent peer session, GPU image generation, Windows/FlaUI test or Minecraft import tested in this cloud session.** Do not release/sign/tag 3.1.0 from this state.

### 2026-11-05 — Torrent source/staging hardening and temporary game mod exit recovery (SOURCE WIP)

- Read local torrent metadata through one bounded file handle; reject duplicate `info` and critical bencode keys; preflight resumed native-torrent stage trees for links before peer writes. New synthetic regression tests, not run.
- Approved temporary game config mods are eligible for automatic restore after the original process exits; new game instances and changed configs block restore and retain original recovery backups. Slow background recovery checks do not run during Gaming. New synthetic service tests, not run.
- Platform build, dependency/API compatibility, peer swarm integration, game pause/resume and full UI regression remain unverified on Windows. No 3.1.0 release.

# Changelog

### 2026-11-05 — Torrent hardening + AI temporary game mods (source WIP)

- Fixed the embedded MonoTorrent/Core dependency-injection and C# accessibility wiring; its GUID-named staging cleanup now bounds recursive traversal and refuses filesystem links. Added synthetic tests; actual peer downloads and Windows build NOT RUN.
- Added an AI-proposed temporary config workflow for paused, PaRuX-managed OFFLINE portable games: UTF-8 .ini/.cfg/.json/.txt, small bounded files, exact Quick Resume PID/start/executable matching, original SHA-256, 5-minute preview, separate human-approved apply, on-disk rollback receipt and explicit restore.
- The local agent has only a consent-gated game_mod_propose tool, not file-write authority. Quick Resume adds AI Mod, Review AI mods, Restore AI mods. Original config text is only returned to the desktop owner and is not exposed to the model by this tool.
- Source-only xUnit, API auth and FlaUI assertions were added, NOT RUN. Temporary mods do NOT yet auto-revert on game exit and often require game restart; online multiplayer, anti-cheat bypass, binary/process memory modification, arbitrary installer execution and multifile torrent publishing are not implemented.

### 2026-11-05 — GOS-07/GOS-13 verified portable ZIP auto-deployment (SOURCE WIP)

- User can explicitly opt in **before** an HTTPS/magnet/.torrent download to auto-deploy one **portable ZIP** when finished. PaRuX requires an independently obtained SHA-256, the eventual .zip file name, an explicit relative non-installer .exe and a game title; no archive-provided scripts, MSI, setup, downloads, executables or games run automatically. Opt-in is OFF by default.
- After normal queue SHA-256 verification, `PortableGameInstallService` re-verifies the source bytes, holds a Windows share lock, preflights bounded ZIP names/count/size/symlinks and exact .exe presence, then uses existing `SafeArchiveExtractor` into an isolated managed partial folder. It commits by directory rename and registers a manual-game entry; failure/cancellation attempts rollback and preserves the original verified archive.
- Download and post-install states are separate, including Pending/Installing/Installed/Interrupted/Failed. IT/EN UI exposes the consent option, title, executable path, status and confirmation; original jobs and unrelated downloads keep existing semantics. Added synthetic ZIP security/rollback tests and a local HTTP-to-install integration test, plus UI assertions. **No build, xUnit, FlaUI, legitimate publisher-trust verification or Windows install performed in this cloud session**.
- This is **not** unattended arbitrary EXE/MSI installation or piracy-source indexing. Multi-file torrents, 7z/RAR, signed installer adapters, launcher/store installers and rollback after a host crash are still NOT IMPLEMENTED.


### 2026-11-05 — GOS-13 integrated BitTorrent in Gaming downloads (source WIP; not Windows-tested)

- Added a pinned **MonoTorrent 3.0.2** MIT library inside PaRuX Server (not an external app) and an `IGameDownloadTransport` implementation for user-provided BTIH magnets and local v1 **single-file** `.torrent` files. The existing owner-authorized queue, Gaming pause/cancellation, SHA-256 publication boundary and non-overwrite semantics are reused. Optional qBittorrent WebUI remains a per-job override; no new source catalogue.
- Added source validation, torrent hash and size checks, isolated stage, peer shutoff at completion, no UPnP/NAT port forwarding, no LAN discovery or continued seeding, and hook for the existing live bandwidth cap. Unsupported multifile/v2 and suspicious files fail closed. No auto installation, executable launch, seeder discovery feed or DRM bypass.
- Added IT/EN source selector, `.torrent` file picker, explicit P2P IP privacy/rights warning, progress bars and active-download-only UI refresh. Owner-only torrent-engine status API and client, synthetic metadata/xUnit and FlaUI assertions, and updated redistributed MIT licence inventory.
- **NOT COMPILED / NOT RUN** in this remote session. NuGet restore, MonoTorrent API correctness, real peer transfer, pause/resume/cancel & cleanup, Gaming hard-off and all Windows FlaUI/physical gates remain open. Not part of any published release.


### 2026-11-05 — L5 manual Steam price quote (WIP, not compiled/tested on Windows)

- Added owner-only POST for an explicitly confirmed, one-shot Steam storefront price. The client reuses Game Discover's existing consent setting, fixed HTTPS host, cookie-free client, bounded response, timeout and request limiter; incoming country is only a requested storefront value, not an authenticated account region.
- Game Discover runtime is now cached by host lifetime rather than request-scoped service provider, so its network lock and rate limiter persist across requests. Price results are discarded when Gaming starts, Discover is disabled, or the saved wishlist item changes during the call.
- Gaming Home wishlist now shows an IT/EN **Check Steam price once** action and a clearly labeled indicative quote/threshold comparison. No scheduled polling, notifications, purchases or account login. New synthetic provider, owner-security and FlaUI regression tests are authored but **NOT RUN**; no real Steam API or device test was performed. The Store appdetails pricing endpoint is undocumented and may change.


### 2026-11-05 — Gaming L5: local Steam wishlist (WIP, not Windows tested)

- Added an offline, versioned local wishlist store with bounded file size, cross-process write lease, atomic replacement, corruption refusal, duplicate validation and explicit opt-out on new items. Separate owner-only Core GET/POST/PUT/DELETE APIs never fetch prices, run timers or touch game files.
- Added shared IT/EN desktop Gaming Home wishlist dialog to save numeric Steam app IDs, target price/currency/store country, enable or disable a stored watch **preference by explicit user confirmation**, and delete entries. No price notifications or actual monitoring are implemented.
- New synthetic `LocalWishlistStoreTests` and owner-route security cases are **authored-not-run**. The new WinUI screen, Server/App/Contracts builds, Windows release gates and gamepad accessibility remain **NOT VERIFIED**. Do not infer readiness for 3.1.0.


### 2026-11-05 — Source merged on main; Windows validation outstanding

PRs #1–#5 were squash-merged onto `main` on 2026-11-05. New code remains **Authored-not-compiled** in this remote session and all new tests remain **Authored-not-run**; neither integration nor publication of release 3.1.0 is claimed.

- **L2 PR #1:** strict same-file Git parser rejects unsupported/unknown metadata, zero-hunk sections and repeated headers, with SHA-256 in-memory preview; Auto requires OS and all mutation tools confined. No live executor/UI or tests run.
- **L3 PR #2:** owner-only two-model Arena Core POST/GET, typed client/DTO, IT/EN statuses, strict model names and 4,000-character prompt cap; added small Advanced Chat Compare button and side-by-side modal with manual refresh/cancel, plus authored-not-run FlaUI Smoke visibility test. No routing/chatbubble redesign or Windows/hardware verification; media free-VRAM presets remain estimates.
- **L8 PR #3:** selected local game guides must match separately verified indexed-source IDs (empty/unknown scopes rejected), and unknown Gaming telemetry blocks GPU admission; no live Chat/OS integration or tests run.
- **L6/L7 PR #4:** per-game suspend proposals with anti-cheat exclusions and monotonic continuity tracking (focus/battery/thermal; reset on changed process, rule edits or missing samples), controller diagnostic/remap previews and a typed consent-gated, PaRuX-owned undo plan; tests authored-not-run; **Needs physical HW**.
- **L4/L5/L9 PR #5:** bounded export previews with desktop-owner-only/Gaming-blocked Core endpoint, shared DTO, authenticated client and bilingual Gaming Home preview dialog; default-off wishlist and public price parser requiring matching store/product/currency/country/freshness; offline license/IT-EN audit scripts. **All new source/tests authored-not-compiled/not-run**. No actual games imported, public API fetching, persisted monitoring, region proof or notifications.
- **Docs PR #6:** reconcile roadmap, README and handoff to the merged source, preserving Windows build, targeted xUnit, full solo FlaUI regression and physical checks as release gates.


### 2026-11-05 - Gaming home: hero, filter rail, save restore and launch-helper UI, IGDB covers, Quick Resume status

- **Games home:** hero for the last played startable game (Play still goes through the launch consent), filter rail with counts (all, favourites, recently played, installed / not installed, store, genre), keyboard / D-pad / bumper movement between tiles, stable AutomationIds. EA app / Battle.net / Ubisoft / Xbox titles are labelled "unverified" (read-only discovery, not yet checked on a real install: **NEEDS PHYSICAL HW**).
- **Game details:** per-game save backup + **restore** (preview, confirmation, automatic pre-restore copy, SHA-256 re-check, plain-language refusals) and per-game **launch helpers** (off by default, .exe only, confirmed, never during Gaming).
- **Covers:** optional **IGDB** source with the user's own Twitch credentials (DPAPI, never in settings); per-game cover override unchanged. IGDB parsing is tested with synthetic JSON only, **not against the live API**.
- **Quick Resume:** status line on the Games home and the start-up recovery results (resumed / failed) in its panel.
- Tests: new xUnit (home rules, restore/hook wording, IGDB parsing) and FlaUI `GamingHomeCompetitiveUiTests` (isolated data root, synthetic games).

### 2026-11-04 — Virtual controller: guided activation, verified HidHide binding, per-game profiles, hot-plug
