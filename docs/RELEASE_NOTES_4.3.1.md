# RBG – PaRuX AI & Gaming OS 4.3.1 release notes (pre-release)

- **Date:** 2026-11-20
- **Channel:** pre-release on GitHub, not promoted to "latest". The Microsoft Store package for 4.3.1.0 is prepared for upload; submission is the owner's step.
- **Build commit:** the tagged commit `v4.3.1` (artifact hashes are in `SHA256SUMS.txt`)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion` = 4.3.1

## Highlights

**AI / Chat**
- **Generation settings** (temperature, top-p, max tokens and more) with saved **presets**, applied by the Core; **edit and resend** your own message; a calmer empty state; the composer tools move below the text box on narrow windows; the version pager shows only when a reply has versions.
- Chat keeps using PaRuX's own managed local runtime. A registered OpenAI-compatible server on this PC is a **comparison target for benchmarks only**; chat is never redirected to it (a deliberate security limit, now stated honestly in Settings).

**Gaming**
- **Smart collections** (saved filters), a **compact top** option (hides the last-played card and Continue shelf), achievements from Steam when the online edition is used and a key is saved.
- **Downloads**: real pause, resume (Range requests) and SHA-256 verification; an interrupted download continues from the kept part when added again. The history never stores a download's address or folder.
- **Controllers**: live view of sticks, triggers and buttons, a guided mapping wizard, rumble test where the pad supports it, and an **Xbox pad without a driver** for games that use XInput (user-mode shim installed per game folder with a receipt; refuses anti-cheat games; never overwrites foreign files).
- Switch library scan no longer lists updates and DLC as games.

**Settings and administration**
- Installed plugins with Remove, a **tamper-evident activity log**, **per-device read-only access**, and **webhooks** (HMAC-signed).
- **Corporate Lite stays offline by construction**: webhooks and Steam are refused by the Core and hidden in the window; the activity log and device access remain.

## Verification

| Check | Result | Evidence |
|---|---|---|
| Release gate (restore, build, publish checks, repository checks) | PASS | `tools/release-gate.ps1 -Restore -SkipAndroidTests` |
| Server xUnit | 14,238 passed / 0 failed / 9 skipped (opt-in live tests) | gate run on the 4.3.1 commit |
| FlaUI regression (Debug build, isolated profiles) | 373 passed / 0 failed / 3 not run (opt-in real-model tests) | full run on the 4.3.1 tree |
| Real-hardware FlaUI on this PC | 8BitDo SN30 Pro live view and mapping, Eden with real Switch ROMs, a real PC game started through consent, real downloads | `RealDevicesAndGamesUiTests`, `RealGameLaunchUiTests`, `RealDownloadsUiTests` |
| Corporate Lite acceptance | strict acceptance PASS (grounded answers 4/4, abstention OK) | 4.3.0 package; unchanged engine |

## Limitations (NOT RUN or not built)

- **Not built (MISSING)**: Epic and GOG achievements, cloud-save sync, owned-store install/repair, a library extension API, user accounts or SSO (roles are read-only per device), OCR.
- **Controllers**: this does not replace HidHide for DirectInput or raw-HID games (the shim covers XInput games only); the physical rumble effect and the 32-bit shim in a real game were not verified; shim DLLs are unsigned; no real game was run with the virtual pad.
- **Not run**: clean-machine MSI install/upgrade/uninstall (needs administrator rights), Android real-device pairing, firewall-blocked, 8 GB RAM and disk-full runs, exFAT, torrent peer and 8 GiB archive tests.
- **Competitors**: cold start is slower than Playnite on the test PC. **No superiority is claimed.**
- Steam achievements were checked against a simulated Steam service; this PC has no registered Steam games.

## Signing

Packages are signed with a **self-signed certificate (CN=Paolo Rubagotti)** with a DigiCert timestamp, and the update manifest is signed with PaRuX's own RSA key. SmartScreen reputation is NOT established. The Microsoft Store package is unsigned by design.

## Upgrade notes

- MSI: use in-app Updates or install the new MSI over the old one. Portable: replace application files and keep `PaRuXData`.
- Large files above the GitHub limit are published as `<name>.part001`, `.part002` plus `<name>.parts.sha256.txt`; join them with `join-release-asset.ps1`.
