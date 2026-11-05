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

## Cloud source-only handoff — 2026-11-05 (draft PRs, no merge)

These are **independent, unmerged draft PRs**, all based on `main` before the change. Status for every newly authored test is **Authored-not-run**, every new C#/PowerShell source is **Authored-not-compiled / not executed**. The previously observed Windows tests on main do not validate them.

| Block | Branch / draft PR | Exact new files or changes | Not yet demonstrated |
|---|---|---|---|
| L2 | `remote/l2-coding-review-policy-20261105` / [#1](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/1) | `src/PaRuX.App/Services/CodingHunkReviewPlanner.cs`; `src/PaRuX.App/Services/CodingSelectiveHunkPreview.cs`; `tests/PaRuX.Server.Tests/CodingHunkReviewPlannerTests.cs`; xUnit project links both sources | Real preimage recheck/transaction executor and actual per-hunk UI integration |
| L3 | `remote/l3-chat-compare-media-presets-20261105` / [#2](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/2) | `src/PaRuX.Contracts/ModelArenaContracts.cs`; `src/PaRuX.Server/{QuickChatComparisonPolicy.cs,QuickChatComparisonPresenter.cs,ModelArenaEndpoints.cs}`; `src/PaRuX.App/Services/ModelArenaClient.cs`; `tests/PaRuX.Server.Tests/{QuickChatComparisonPolicyTests.cs,BrowserOwnerAndLanHardeningTests.cs}` | Live Chat UI/command wiring, Windows route/security tests and real GPU/model-fit checks |
| L8 | `remote/l8-local-game-chat-resource-policy-20261105` / [#3](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/3) | `src/PaRuX.Server/Orchestration/Gaming/LocalGameChatPolicy.cs`; `tests/PaRuX.Server.Tests/LocalGameChatPolicyTests.cs` | Integration with Chat, gaming event checks and physical GPU isolation |
| L6/L7 | `remote/l6-l7-suspend-controller-planners-20261105` / [#4](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/4) | `src/PaRuX.Server/Orchestration/Gaming/{GameSuspendControllerPolicies.cs,GameSuspendDebouncePolicy.cs}`; `tests/PaRuX.Server.Tests/{GameSuspendControllerPoliciesTests.cs,GameSuspendDebouncePolicyTests.cs}` | Live focus/battery/thermal events, anti-cheat confidence, controller/driver tests (**Needs physical HW**) |
| L4/L5/L9 | `remote/l4-l5-external-library-prices-20261105` / [#5](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/5) | `src/PaRuX.Server/Orchestration/Gaming/ExternalLibraryPricePolicies.cs`; `src/PaRuX.Server/Orchestration/Gaming/LocalWishlistPolicy.cs`; `tests/PaRuX.Server.Tests/ExternalLibraryPricePoliciesTests.cs`; `tests/PaRuX.Server.Tests/LocalWishlistPolicyTests.cs`; `tools/check-direct-third-party-coverage.ps1`; `tools/audit-it-en-literal-pairs.ps1` | Real vendor export compatibility, API consent/wiring, PowerShell audit results |
| Documentation | `remote/l-roadmap-handoff-20261105` / draft #6 | L section of `PARUX_EXECUTION_PLAN.md`, `CHANGELOG.md`, `README.md`, this `HANDOFF.md` | Windows gates after any code merge; no change to release runbook |

### Windows validation commands (one branch at a time; no parallel FlaUI)

Start from a clean Windows working tree with the .NET SDK and PaRuX prerequisites. Review each independent PR's exact diff, and **check out that PR branch** before its associated filter; these tests do not exist on `main` yet. Example, repeat the checkout/build/filter trio with the corresponding branch and test filter for #2 through #5.

```powershell
git fetch origin
git switch --detach origin/remote/l2-coding-review-policy-20261105
git diff --check origin/main...HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~CodingHunkReviewPlannerTests"

git switch --detach origin/remote/l3-chat-compare-media-presets-20261105
git diff --check origin/main...HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~QuickChatComparisonPolicyTests|FullyQualifiedName~BrowserOwnerAndLanHardeningTests"

git switch --detach origin/remote/l8-local-game-chat-resource-policy-20261105
git diff --check origin/main...HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~LocalGameChatPolicyTests"

git switch --detach origin/remote/l6-l7-suspend-controller-planners-20261105
git diff --check origin/main...HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~GameSuspendControllerPoliciesTests|FullyQualifiedName~GameSuspendDebouncePolicyTests"

git switch --detach origin/remote/l4-l5-external-library-prices-20261105
git diff --check origin/main...HEAD
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-public-safety.ps1
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests --filter "FullyQualifiedName~ExternalLibraryPricePoliciesTests|FullyQualifiedName~LocalWishlistPolicyTests"
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-direct-third-party-coverage.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-direct-third-party-coverage.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/audit-it-en-literal-pairs.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File tools/audit-it-en-literal-pairs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/validate-license-inventory.ps1
```

Review the draft PRs and resolve all build/test failures. When an integration candidate includes all **reviewed** work in a separately tested working tree, run the full Server suite, then run the complete UI suite **alone**, without any other FlaUI instance. Never infer green tests for PRs from the previous 12,410-pass main baseline.

```powershell
dotnet build src/PaRuX.App -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-sbom.ps1 -Scope windows -ProductVersion 3.0.0
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-ui.ps1 -Suite Regression
```

After successful software gates: manually verify setup transitions, role profiles, document-skills controls, deletion confirmation, current-game resource arbitration and GPU recovery; physically test controllers, HidHide restore, virtual pad, per-game suspend, supported store exports/IGDB, and Corporate Lite low-memory behavior. Treat XOutput equivalence, 8 GB compatibility and hardware acceptance as **unverified**, not as promises.

### Compile/runtime risks to inspect

- L2: per-hunk parser requires matched `--- a/...` and `+++ b/...` identities and bounded hunk counts; quoted/renamed/binary/no-newline diffs may be unsupported. New in-memory preview checks old lines and emits a SHA-256 preimage fingerprint but does not re-read the actual file, handle BOM/encodings or apply a patch. Verify linked App source compiles in both App and xUnit projects, and do not map `bypass` to unsandboxed execution.
- L3: Arena and PersonaService already exist. The draft now has a desktop-owner-gated Core POST/GET and typed App client with a 4,000-character prompt cap. A visual Chat action/renderer is **not** connected. Verify both endpoint auth and response serialization on Windows; estimated media presets must not replace actual resource admission.
- L8: `ChatDocumentScope.SourceIds` empty means all eligible sources, so the new planner rejects empty, invalid or over-64 source scopes; Core Gaming hard-off still owns enforcement.
- L6/L7: anti-cheat list is deliberately incomplete; unknown classification, protected process names and user exclusion entries block suspend proposals. The new 3/5/10-second continuity tracker requires caller-provided monotonic timestamps, verified PID/start-time stamps and samples no more than 2 seconds apart; there is **no sensor/event integration**, and controller curves/undo are previews, not drivers. All are **Authored-not-compiled / Needs physical HW**.
- L4/L5/L9: export JSON shapes are conservative examples, not general coverage of vendor versions; the wishlist and matched currency/product price decisions are models only (no API polling, persisted state, deduped notifications or store-region proof); source-only i18n scan cannot detect dynamic/XAML translations.

**Release boundary:** `3.1.0` stays UNRELEASED. No tags, MSI, APK, portable ZIP or GitHub release creation is authorized in this cloud session. The original Windows-only release runbook above is unchanged; only use it after all gates pass and the maintainer performs authorized local signing.
