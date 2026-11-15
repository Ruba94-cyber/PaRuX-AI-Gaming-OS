# RBG – PaRuX AI & Gaming OS 4.2.0 release notes (release candidate)

- **Date:** 2026-11-15
- **Channel:** release candidate. Not stable and not published until the owner publishes it; 4.0.1 remains the stable-channel default until then.
- **Build commit:** the commit the release artifacts are built from (recorded with the artifact hashes at publish time; clean tree required)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = 4.2.0

## New branding

The product is now shown as **RBG – PaRuX AI & Gaming OS** (window title, About, executable and assembly metadata, Company "RBG"). Android shows "RBG – PaRuX" in-app. Technical identifiers are unchanged (package ids, namespaces, data folders, ports, API routes), so upgrades keep settings, models, pairings and data.

## Highlights

- **Gaming:** on-screen controller keyboard; per-game launch profiles with a UI; Games home layout fix.
- **Chat / UI:** composer footer controls wrap inside the window at narrow widths; Italian first-run text for the no-runtime path is fully localized.
- **Corporate Lite:** CPU-only package measured on a laptop-class PC (see Verification).
- **Documentation:** guides, README and screenshots.
- **Release engineering:** all first-party PEs signed in MSI and portable; split assets for files above 2 GiB.

## Fixes

- Italian onboarding showed an English "Setup is incomplete" line on the no-runtime path; now localized.
- Chat composer controls overflowed at narrow widths; they now wrap.
- UI test harness: waits for pane policy before toggling; the accessibility sweep exempts only pointer-only ScrollBar parts.

## Signing

Packages are signed with a **self-signed certificate (CN=Paolo Rubagotti)** with a DigiCert timestamp. It is not issued by a public authority and **SmartScreen reputation is NOT established**. Windows may warn on first run: trust the certificate yourself, or accept the warning after checking the SHA-256 against the published value.

## Verification

| Check | Result | Hardware / OS | Evidence |
|---|---|---|---|
| Release build | 0 errors | Windows 11 | build log |
| Server xUnit | 14,047 passed / 0 failed / 9 skipped | Windows 11 | run on `d92dd420` |
| FlaUI regression | 329 / 329 | Windows 11 | run on `13dc0cc7` |
| Android unit tests | 53 / 53 | Windows 11 (Gradle) | `:app:testDebugUnitTest` |
| Corporate Lite, CPU only | grounded 4/4; time to first token 15-17 s; 12-13 tokens/s; peak RSS 4.4 GB; 0 non-loopback connections | Ryzen 7 8840U, 32 GB RAM (measured on that hardware only) | Corporate strict gate |

An RTX 5080 was not attached, so **no RTX / eGPU figures** are claimed. The CPU figures do not predict other machines.

## Compatibility and requirements

Windows 11 x64 (details: `docs/guides/INSTALL.md`). Base Chat and document retrieval run locally without cloud inference. The MSI and Portable packages do not bundle a chat model; only the Corporate Lite with-model package does. Speed depends strongly on CPU/GPU and RAM.

## Upgrade notes

- MSI: use in-app Updates or install the new MSI over the old one. Portable: replace application files and keep the `PaRuXData` folder.
- Back up your data folder first if it holds anything you cannot recreate.
- Repository rename: the project now lives at `Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS`. The in-app update channel and the signed update manifest keep the previous repository identifier on purpose, so installed 4.1.x clients keep accepting updates; GitHub redirects the old name to the new repository (checked on 2026-11-15 for the API and release pages). An update through that redirect has not been exercised end to end before this release exists.

## Joining split release assets

Files above the GitHub limit are published as `<name>.part001`, `.part002`, ... plus `<name>.parts.sha256.txt`. Put all parts and the list in one folder and run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\join-release-asset.ps1 -FirstPart .\<name>.part001

The tool checks every part and the final SHA-256 before writing the result; do not use the file if it reports an error.

## Limitations (NOT RUN or not established)

- MSI install, upgrade and uninstall on a clean machine: NOT RUN (unless the owner adds evidence later).
- Physical controller button presses, TV / HDR output, eGPU and RTX numbers: NOT RUN.
- Firewall-blocked network, real 8 GB PC, OCR, disk-full: NOT RUN. A 30,000-page synthetic PDF gate was run earlier on the reference PC (plan item QA-037, earlier build); it was not repeated for 4.2.0.
- No roles or audit log in this release.
- Android: pairing with a real Core, a physical device and an upgrade from an older APK: not tested.
- Microsoft Store package: not certified.
- Self-signed signing only; SmartScreen reputation not established.

## Links

[Install](guides/INSTALL.md) · [Troubleshooting](guides/TROUBLESHOOTING.md) · [Plan](../PARUX_EXECUTION_PLAN.md) · [Changelog](../CHANGELOG.md)
