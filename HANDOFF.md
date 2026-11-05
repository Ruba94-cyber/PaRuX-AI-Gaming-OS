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
