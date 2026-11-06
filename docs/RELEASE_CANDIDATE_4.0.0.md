# PaRuX 4.0.0 — release candidate status (unsigned, unpublished)

Built and verified on the Windows development PC (no GitHub Actions). Artifacts live in
`artifacts/release-candidate/4.0.0/` (git-ignored) with `SHA256SUMS.txt`.

| Artifact | Build | Verification actually performed |
|---|---|---|
| `PaRuX-4.0.0-win-x64.msi` | `scripts/build-msi.ps1` (WiX 7, EULA accepted by owner) | Built; administrative-image extraction OK, `PaRuX.exe`/`parux.exe` report 4.0.0. **Install/uninstall NOT run** (needs an elevated shell; `scripts/test-msi.ps1 -CurrentMsi <msi>`). |
| `PaRuX-4.0.0-portable-win-x64.zip` | `scripts/build-portable.ps1 -Smoke` | ZIP content check (required files, no models/indexes/tokens); extracted app started its Core with an isolated data root. |
| `PaRuX-4.0.0-Corporate-Lite-Offline-win-x64.zip` | `scripts/build-corporate-lite.ps1 -Smoke` (Qwen3-4B Q4_K_M, CPU llama.cpp, MiniLM) | ZIP check; offline start, downloads refused (403), document search answers without a model. Not measured on an 8 GB corporate PC. |
| `PaRuX-4.0.0-android-release-UNSIGNED.apk` | Gradle `assembleRelease` | Unit tests + `lintRelease` pass; a debug-signed COPY installed and launched on an emulator (pairing screen, no crash). **Not tested on a physical device; real pairing with a Core not exercised.** |

## Test evidence (this PC, 2026-11-05/06)
- Server xUnit, run in alphabetical chunks: all chunks green after fixes (MonoTorrent notice row, ps1-shim support in
  `tools/test-agent-platform-v2.ps1`, suspend theory typing, duplicate-mod test, read-only git fixture cleanup).
  Known flake: some chunks occasionally lose the test host (exit code -1, no managed exception, no dump); every chunk had a clean pass on re-run. Cause not identified.
- Final 4.0.0 source: Server xUnit all chunks green (no host exits this run); FlaUI Smoke 35/35; FlaUI Regression run in chunks 283/283 green (one earlier full run: 287/290 -> 3 fixed; hosts occasionally hang/exit at teardown with the app orphaned - tests had passed). Earlier note: FlaUI Smoke 35/35. FlaUI Regression 287/290; the 3 failures were fixed (guide-dialog empty state is now exposed to UIA, shared-read of `manual-games.json`, Quick Resume scrolling/assertions) and re-run green individually. A final full Regression re-run is still advised.
- FlaUI is serialised machine-wide with `tools/Invoke-ExclusiveDesktop.ps1` (open-handle lock in
  `%LOCALAPPDATA%\PaRuX\TestCoordination`, refuses to start while another UI test runner is active, never kills anything).

## Not verified / blockers
- MSI install, upgrade-from-3.0.0 and uninstall (requires elevation).
- Physical Android device; physical controllers/HW features listed as "Needs physical HW" in the plan.
- Corporate Lite on a real low-end PC; answer quality of the 4B model.
- Selective-hunk review (L2) real end-to-end against a user repository.

## Signing and publication checklist (owner, manual — nothing below was done)
1. Clean tree at the intended commit; `git pull`.
2. Env (never echo secrets): `PARUX_ACCEPT_WIX7_EULA=1`; `PARUX_SIGN_PFX` + `PARUX_SIGN_PFX_PASSWORD` (from `Documents\FirmaApp-PaoloRubagotti`);
   `PARUX_RELEASE_PRIVATE_KEY` + `PARUX_RELEASE_KEY_PASSWORD` (manifest key, `Documents\LocalAI`).
3. `scripts/build-msi.ps1 -Release` (signs MSI, writes provenance + update manifest). Optionally `scripts/test-msi.ps1` elevated.
4. Rebuild portable + Corporate Lite from the same commit; sign the executables inside if required.
5. `git tag v4.0.0`; Android: `PARUX_ANDROID_KEYSTORE` + passwords, `scripts/publish-android-apk.ps1 -Tag v4.0.0` (or `-LocalOnly`).
6. `scripts/verify-release-assets.ps1`; generate SHA-256; `gh release create v4.0.0` with honest status labels.

## Plan items NOT implemented (need external credentials, hardware, or multi-session design)
- UAI-08..12 Microsoft 365 / Google connectors and cross-app work (OAuth client registrations and real accounts required), UAI-19 connector ecosystem.
- L1 local tuning runtime; L6/L7 OS-event/sensor and controller driver wiring (physical hardware); UAI-14/15/18 depth and independent competitive evaluation.
- Many P1/P2 items remain PARTIAL in PARUX_EXECUTION_PLAN.md; none were declared DONE by this validation pass.
