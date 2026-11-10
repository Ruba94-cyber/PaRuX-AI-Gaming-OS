# PaRuX 4.1.0 release candidate `4.1.0-rc1` (local only, not published)

Gate PKG-001. Built 2026-11-10 on the Windows development PC. Nothing was pushed, tagged remotely, uploaded or released.

## Source identity

| Item | Value |
|---|---|
| Version commit | `c288afb9077010e50ebea9a9a5766a8e3793e832` "chore(release): 4.1.0" (parent `b6844daf`) |
| Local tag | `v4.1.0-rc1` on `c288afb9` (never pushed). MSI versions cannot carry a prerelease suffix, so the RC label exists only in this tag and this document; all artifacts say 4.1.0. |
| Packaging-script fix | `25456296` `scripts/build-corporate-lite.ps1`: new `-NoModel` switch, and the CPU-runtime size floor lowered from 64 KiB to 4 KiB (current llama.cpp ships a 9.7 KB `llama-server.exe` launcher). Default behaviour unchanged. |
| Build trees | Detached worktree `.claude/worktrees/rc-4.1.0`. **MSI and APK were built at `c288afb9`. Portable and Corporate Lite were built at `25456296`** (they need the script fix; application source is identical to `c288afb9`, only the embedded commit differs). |
| Version bump | App/Server csproj 4.1.0 (File/Assembly 4.1.0.0), Android versionName 4.1.0, versionCode 21 to 22. `PaRuX.Cli` and `PaRuX.UpdateHelper` keep their own independent 2.0.0 version (unchanged convention). No xUnit test asserts the product version; `dotnet build PaRuX.sln -c Debug -p:Platform=x64` succeeded (0 errors). |

Artifacts (git-ignored) are under `.claude/worktrees/rc-4.1.0/`: `artifacts/installer/`, `artifacts/portable/`, `artifacts/corporate-lite/`, `src/PaRuX.Android/app/build/outputs/apk/release/`. Logs, hashes, release manifest, logcat and screenshot are in `.ai/artifacts/pkg-rc1/` of the main checkout (`SHA256-and-sizes.txt`, `release-candidate-manifest.json`).

## Per-distribution results

Windows Authenticode signer: `CN=Paolo Rubagotti`, self-signed, SHA-1 thumbprint `F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8`, RFC 3161 timestamped (DigiCert).

| Distribution | File | Size (bytes) | SHA-256 |
|---|---|---|---|
| Windows MSI | `PaRuX-4.1.0-win-x64.msi` | 823173120 | `5d8be2668421f090fe035a717504b8bc67901f7369435b796f78e4928503f4c3` |
| Update envelope | `PaRuX-4.1.0-win-x64.update.json` | 1314 | `d1125ead03074012d71e9aab67e48fe112a49c7fd043f6a19c075900507f529c` |
| Release provenance | `PaRuX-4.1.0-release-provenance.json` | 302520 | `666da4c107f7108affdd32e3386088783aa2ba2c4c61e276f8fe0aad3f597d96` |
| Windows Portable | `PaRuX-4.1.0-portable-win-x64.zip` | 207745211 | `32ce04ab57f7da999e90ec23b28dc1163d181c616f025ef4a82d8bbfb56c5e47` |
| Portable Corporate (Lite, no model) | `PaRuX-Corporate-Lite-Offline-win-x64.zip` | 280482021 | `0c9c5591926489c6a3ceda8dfd3d8aa47fd63a9f56ff11dafd0646be290a110d` |
| Android APK | `PaRuX-4.1.0-android.apk` | 4233998 | `3e83af47f9ec7fff346f8a2a043e769b8fd4f5f5eb1247113a3fa8b5fbb3b6a2` |

| Distribution | Signed by | Signature verification | Launch / install test | NOT verified |
|---|---|---|---|---|
| MSI | Authenticode (thumbprint above) + RSA update envelope | `signtool verify /pa /v`: successfully verified, 0 errors (the self-signed root is trusted on this PC; on another PC expect a chain-trust failure / Unknown publisher). `Get-AuthenticodeSignature`: Valid. Primary EXEs (PaRuX, PaRuX.Server, parux CLI, UpdateHelper) all Valid, same thumbprint, version `4.1.0+c288afb9...`. `verify-release-assets.ps1`: PASS (envelope + MSI bytes, 1611-file payload equals provenance via `msiexec /a` administrative extraction, CUDA baseline, embedded commit in 4 binaries, publish dir equals payload). | Install test NOT RUN: process not elevated, no UAC triggered. Static validation only (above). | Install, upgrade from 4.0.1, uninstall, update-helper elevation path. |
| Portable | **Not signed** (`build-portable.ps1` has no signing step; EXEs report NotSigned) | n/a; script ZIP check OK (1165 entries, no models/indexes/tokens) | Extracted to `%TEMP%`, launched with `PaRuX (portable data).cmd` (fresh PaRuXData): UI Automation found main window "PaRuX AI & Gaming OS"; Core healthy on a non-reserved loopback port (`GET /api/v1/health` 200); folder moved to a new path and relaunched: window + healthy again, `settings.json` byte-identical across the move, endpoint re-written. | Real chat/inference (no model), Vulkan runtime on other GPUs, SmartScreen behaviour. Settings persistence shown by file identity, not by changing a setting in the UI. Closing the window leaves the tray app running; the test stopped only processes whose path was inside the test folder. |
| Portable Corporate | **Not signed** | script ZIP check OK (1170 entries, CPU runtime SHA-256 verified, zero models) | Same procedure with `Start PaRuX Corporate Lite.cmd`: window found, Core healthy, move + relaunch OK, settings hash unchanged after the move. | Built WITHOUT a bundled model (owner must confirm the model first): no chat, no offline 403 download check, no import/role test, no low-end CPU test. `-Smoke` not used (own UIA check instead). |
| APK | Android release keystore, alias `parux-release`; signer cert SHA-256 `a33f38eac6a56904389515ce54ccddfd54e45e21869e51d81de903ead3b4a2f8` (`CN=PaRuX`) | `apksigner verify --print-certs`: Verifies (only scheme v2 reported true; v1/v3 false). aapt2: `com.parux.android` versionCode 22, versionName 4.1.0. `verify-release-assets.ps1 -RequireAndroid`: PASS (22 > 21). | Emulator AVD `PaRuX_Test` (not a device): installed, MainActivity resumed, process alive 30 s, 0 FATAL/ANR in logcat, pairing screen captured (`.ai/artifacts/pkg-rc1/apk-launch.png`). The AVD held an older debug-signed build (versionCode 19), uninstalled first because of the signature mismatch; emulator then shut down. | Physical device, pairing with a real Core, upgrade over a release-signed 4.0.1. |

`publish-android-apk.ps1` requires `-Tag v4.1.0` (exact app version); it was run with `-LocalOnly`, so no `v4.1.0` tag was created.

## Distribution compatibility matrix (from scripts/docs, not runtime-tested per feature)

| Area | MSI | Portable | Portable Corporate | Android |
|---|---|---|---|---|
| Desktop app + Core | yes | yes | yes (AI + documents shell) | no (companion to a paired Windows Core) |
| Local inference runtime bundled | llama.cpp Vulkan b10549, CUDA 13 + cudart baseline (hash-locked), uv | llama.cpp Vulkan only | llama.cpp CPU only | none (inference runs on the PC) |
| Bundled model | none | none | none in this build (script supports one) | none |
| Gaming mode / library | yes | yes | no | remote work obeys Gaming hard-off |
| Media / ComfyUI / CUDA stack | optional, on request | optional on request; no CUDA/media pack shipped | no | job supervision only |
| Terminal CLI (`parux.exe`) | yes | opt-in (`-IncludeCli`, not in this build) | no | no |
| Update Helper / in-app signed update | yes | no (replace the ZIP) | no | no |
| Data location | per-user profile | `PaRuXData` beside the exe (portable-data launcher) | `PaRuXData` beside the exe | Keystore-encrypted credentials |
| Internet / cloud | local-first, Internet off by default | same | offline by default; separate IT-approved GGUF download launcher | LAN HTTPS only |
| Authenticode | yes (self-signed) | no | no | APK signature only |

## Known limits

- Authenticode certificate is self-signed; trust on other machines is not established.
- No clean-machine or VM install test; MSI install/upgrade/uninstall not run (needs owner elevation). The `docs/RELEASE_READINESS.md` verdict (NOT RELEASE READY) is not changed by this packaging gate.
- No physical Android device; emulator only.
- Corporate Lite built without a bundled model; the model choice is the owner's decision.
- Portable ZIPs are not Authenticode-signed (the script has no signing step).
- `/api/v1/health` reports `"version":"1.1.0"` in every edition, which is not the product version; not investigated.
- The Corporate build overwrites the shared portable stage, so the Standard portable was rebuilt after it; the hashes above are from the final run.
- Only the Debug solution build was run for this gate; no xUnit/FlaUI suites and no SBOM were generated.
