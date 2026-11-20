# RBG – PaRuX AI & Gaming OS 4.3.0 release notes (pre-release)

- **Date:** 2026-11-20
- **Channel:** pre-release on GitHub, not promoted to "latest". The Microsoft Store package for 4.3.0.0 is prepared for upload; submission is the owner's step.
- **Build commit:** the tagged commit `v4.3.0` (artifact hashes are in `SHA256SUMS.txt`)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = 4.3.0

## Highlights

**Gaming**
- The Games page is split into **Library, Downloads, Add & import, Emulators & ROMs and Gaming settings**. The library shows the most recently played game and its cover first, with a compact toolbar and a View menu (grouping, cover size).
- **Import from Playnite**: reads a copy of your Playnite library and adds your programs and emulator games with covers, favourites, hidden flags and play time. Playnite is never modified; Steam, Epic and GOG titles stay with PaRuX's own discovery.
- **Emulators**: detection, ROM folder scan, saved per-emulator setup that survives a restart, and about 120 known emulators (definitions derived from Playnite's MIT-licensed list; every one is marked unverified until tested against an installed emulator). PaRuX never downloads emulators.
- **Pick for me**, **library insights** (hours, most played, ratings, status), **notes per game**, **export** of the library to CSV or JSON (no file paths) and **restore** of that data on another PC (nothing you already set is overwritten).
- TV/Console: larger section strip and a more compact hero so covers show sooner.

**AI**
- A **prompt library** in the chat box (ready-made prompts and your own, with fill-in fields), a **Ctrl+K command palette**, **export of all chats** as readable Markdown files, and a first-model suggestion that suits PCs without a large GPU (it was a 13 GB model on integrated graphics).
- The empty Chat now offers "Set up local chat" when no engine is installed.

**Fixes**
- The Games page no longer crashes when the window is narrowed while a game card is open (a WinUI layout divided by zero).
- Emulator detection no longer fails on a slow or busy disk (it exceeded a ten-second budget).
- Italian: long Settings category names wrap instead of being cut off; the chat footer model label is localized; the Projects intro is shorter.
- The Downloads destination field now fills its row.

## Signing

Packages are signed with a **self-signed certificate (CN=Paolo Rubagotti)** with a DigiCert timestamp, and the update manifest is signed with PaRuX's own RSA key. The certificate is not issued by a public authority and **SmartScreen reputation is NOT established**: Windows may warn on first run. The Microsoft Store package is unsigned by design; Partner Center signs it.

## Verification

| Check | Result | Hardware / OS | Evidence |
|---|---|---|---|
| Release build and shipped-project publish | 0 errors; all five shipped Windows projects publish | Windows 11 | release gate |
| Server xUnit | 14,105 passed / 0 failed / 9 skipped | Windows 11 | gate run on `f98c1881` |
| FlaUI regression (Debug build, isolated profiles) | 295 / 296 on the first run; the one failure (Hidden titles test) depended on whether a virtualized tile was on screen and was fixed in the test; its class then passed 14/14 twice | Windows 11, Ryzen 7 8840U / Radeon 780M, 200% display scale | final regression on the release commit |
| Android unit tests, lint, release APK | 53 / 53 passed; lint and assemble succeeded | Gradle on Windows | gate run |
| Public-repository safety check | OK | — | `tools/check-public-safety.ps1` |

## Compatibility and requirements

Windows 11 x64 (details: `docs/guides/INSTALL.md`). Base Chat and document retrieval run locally without cloud inference. The MSI and Portable packages do not bundle a chat model; the Corporate Lite with-model package bundles Qwen3-4B Q4_K_M.

## Upgrade notes

- MSI: use in-app Updates or install the new MSI over the old one. Portable: replace application files and keep the `PaRuXData` folder.
- Back up your data folder first if it holds anything you cannot recreate.
- Large files above the GitHub limit are published as `<name>.part001`, `.part002` plus `<name>.parts.sha256.txt`; join them with `join-release-asset.ps1` (it verifies every part and the final SHA-256).

## Limitations (NOT RUN or not established)

- **Not measured against competitors on equal terms**: cold start to a window is slower than Playnite and Locally Uncensored on the test PC (about 0.7-1.3 s against 0.2-0.5 s); no first-token timing on a shared model; no 2,000-title search timing on other apps. See `docs/COMPETITOR_MEASURED_2026-11-19.md`. **No superiority is claimed.**
- **Physical hardware**: no physical controller or TV pass, no RTX/eGPU figures, no clean-machine MSI install/upgrade/uninstall, no firewall-blocked or 8 GB Corporate run in this release.
- **Emulator definitions** imported from Playnite are unverified against installed emulators; RetroArch-style emulators that need a core are not auto-imported from Playnite.
- **Not built**: achievements, cloud-save sync, owned-store install/repair (Hydra/Heroic class) remain MISSING.
- Playnite import reads Playnite's LiteDB files through LiteDB 5.0.21 as untyped documents; LiteDB carries a known advisory for typed object mapping, which this code does not use.
- The FlaUI regression ran on the Debug build with a published Core; the packaged MSI was not driven end to end by FlaUI.
