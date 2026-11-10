# PaRuX 4.1.0 release record (built from `348a433c`, local artifacts, not published by the build agent)

Gate PKG-001. Final build 2026-11-10 on the Windows development PC. The build agent pushed nothing, created no GitHub release and uploaded nothing; publication is the owner's step.

An earlier rc build at `c288afb9` (local tag `v4.1.0-rc1`) is **stale** (product code changed afterwards) and superseded by the artifacts below. Its artifact hashes are intentionally not repeated here.

## Source identity

| Item | Value |
|---|---|
| Build commit | `348a433c4d6db081e659c1982c336946e839949c` (main) |
| Local tags | `v4.1.0` on `348a433c` (not pushed by the build agent); `v4.1.0-rc1` on `c288afb9` (stale, kept) |
| Build tree | detached worktree `.claude/worktrees/rc-4.1.0` at `348a433c`, clean, old outputs deleted first |
| Versions | App/Server 4.1.0 (File/Assembly 4.1.0.0), Android versionName 4.1.0 / versionCode 22. `PaRuX.Cli` and `PaRuX.UpdateHelper` keep their own 2.0.0 version (unchanged convention). |
| Packaging-script fix in this build | `25456296` `scripts/build-corporate-lite.ps1`: `-NoModel` switch; CPU-runtime size floor lowered from 64 KiB to 4 KiB (current llama.cpp ships a 9.7 KB `llama-server.exe` launcher). |

Upload set: `.ai/artifacts/release-4.1.0/` of the main checkout (git-ignored). Build/verification logs, hashes and the verifier manifest: `.ai/artifacts/pkg-rc1/` (`final-*` files).

## Artifacts

Windows Authenticode signer: `CN=Paolo Rubagotti`, self-signed, SHA-1 thumbprint `F73FDA46DD3A0A7DE82C0E479AE1F1E5091416F8`, RFC 3161 timestamped (DigiCert).

| File | Size (bytes) | SHA-256 |
|---|---|---|
| `PaRuX-4.1.0-win-x64.msi` | 823173120 | `c2d5fe4fc3721b492cca96e5afaf29aecea1e0eca80ecc0fcc1e28c28a71bacf` |
| `PaRuX-4.1.0-win-x64.update.json` | 1289 | `3a9ba0caad0592f61afd7536082056f7697197925bd8f2f051bfdef3ec2846b4` |
| `PaRuX-4.1.0-release-provenance.json` | 302521 | `83b80e2ea10d5aac21aea58e20ddff10407e6be2e0c77a1bf92d7b6381529bf3` |
| `PaRuX-4.1.0-portable-win-x64.zip` | 207746590 | `910f4b1507d2aa18573dc3fbe268193258875702f7e694f31e428130f4e052b6` |
| `PaRuX-Corporate-Lite-Offline-win-x64.zip` (no model) | 280483402 | `d7ea4a6b882efe16a768dc063c0bcdae5f9098ecb11bfca29cbfd2b60b61e6e7` |
| `PaRuX-4.1.0-android.apk` | 4233998 | `83ae6f4462522e7a5ae21712baf77e8c0aab72971023a09f27ee93dbc5d89779` |

## Verification results

| Distribution | Signed by | Signature / integrity verification | Launch / install test | NOT verified |
|---|---|---|---|---|
| MSI | Authenticode (above) + RSA update envelope | `signtool verify /pa /v`: successfully verified, 0 errors (the self-signed root is trusted on this PC; another PC will show a chain-trust failure / Unknown publisher). `Get-AuthenticodeSignature`: Valid. `verify-release-assets.ps1 -RequireAndroid` PASS: envelope + MSI bytes, 1611-file payload equals provenance via `msiexec /a` administrative extraction, CUDA baseline, embedded commit `348a433c...` in the 4 primary binaries, Android versionCode 22 > 21. | Install NOT RUN: process not elevated, no UAC triggered; static `msiexec /a` check only. | Install, upgrade from 4.0.1, uninstall, update-helper elevation path. |
| Portable | **Not signed** (script has no signing step) | script ZIP check OK (1165 entries, no models/indexes/tokens) | Fresh extract in `%TEMP%`, `PaRuX (portable data).cmd`: UI Automation found main window "PaRuX AI & Gaming OS"; Core healthy (`/api/v1/health` 200, version `4.1.0`); folder moved to a new path and relaunched: window + healthy, `settings.json` byte-identical across the move. | Settings persistence shown by file identity, not by changing a setting in the UI; SmartScreen behaviour; other GPUs. |
| Portable Corporate | **Not signed** | script ZIP check OK (CPU runtime SHA-256 verified, zero models) | Same procedure with `Start PaRuX Corporate Lite.cmd`: window, healthy (4.1.0), move + relaunch OK, settings unchanged. | Built WITHOUT a bundled model (owner must confirm it): no chat, no offline 403 download check, no import/role test, no low-end CPU test. |
| APK | Release keystore alias `parux-release`; signer cert SHA-256 `a33f38eac6a56904389515ce54ccddfd54e45e21869e51d81de903ead3b4a2f8` (`CN=PaRuX`) | `apksigner verify --print-certs`: Verifies (only scheme v2 reported true). aapt2: `com.parux.android` 4.1.0 / 22. | AVD `PaRuX_Test` (emulator, not a device): clean install, MainActivity resumed, process alive 30 s, 0 FATAL / 0 ANR in logcat; screenshot `.ai/artifacts/pkg-rc1/final-apk-launch.png`; emulator shut down. | Physical device, pairing with a real Core, upgrade over a release-signed 4.0.1. |

### Live check: freshly built Portable on the onboarded isolated data root

Portable extracted from the final ZIP and started with `PARUX_DATA_ROOT` and `PARUX_LOCALAI_STATE_DIR` = `.ai/artifacts/hw-data3` (RTX 5080, CUDA runtime from that root). `/api/v1/health` reported version `4.1.0`. The app started the Standard text runtime on its own at launch (llama-server PID **6008**, Qwen3-4B-Q4_K_M Standard GPU, VRAM 267 MiB to 4324 MiB). An explicit `Standard` request then answered "Already running" with PID **6008**, and a second identical `Standard` request left the PID unchanged (6008): warm reuse confirmed. `Off` stopped the runtime ("Stopped."); after closing everything started from the extract (no leftovers), no llama-server remained and VRAM was back to 267 MiB (idle before the test: 267 MiB). Note: because the runtime was already running at app start, this did not exercise a cold start through the explicit request. Log: `.ai/artifacts/pkg-rc1/final-live-hw-data3.log`.

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
- No clean-machine or VM install test; MSI install/upgrade/uninstall not run (needs owner elevation). `docs/RELEASE_READINESS.md` governs the readiness verdict; this packaging gate does not change it.
- No physical Android device; emulator only.
- Corporate Lite built without a bundled model; the model choice is the owner's decision.
- Portable ZIPs are not Authenticode-signed (the script has no signing step).
- The Corporate build overwrites the shared portable stage, so the Standard portable was rebuilt after it; hashes above are from the final run.
- No xUnit/FlaUI suites or SBOM were run by this packaging pass (see `docs/QA_EVIDENCE.md` for the test runs).
