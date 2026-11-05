# PaRuX hand-off (written 2026-11-05) - continue on another PC

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
