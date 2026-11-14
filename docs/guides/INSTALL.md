# Install RBG – PaRuX AI & Gaming OS

Short name: RBG – PaRuX. Status: **BETA** (v4.1.0 pre-release). Read [Known limits](#known-limits) first.

## Before you start

- Windows 10 or newer, x64. Linux and macOS are not supported.
- Requirements by use case: see [README requirements](../../README.md#system-requirements).
- Pick a package. Release assets vary per release; check the
  [GitHub Releases](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS/releases) page. If no assets are listed, no public installer exists yet.

| Package | Use it when | Guide |
|---|---|---|
| MSI installer (`PaRuX-<version>-win-x64.msi`) | Normal PC, per-machine install, in-app update | this page |
| Portable ZIP | No install, USB or relocatable folder | [PORTABLE.md](PORTABLE.md) |
| Corporate Lite (CPU, offline, AI + documents) | Managed or offline PCs | [CORPORATE.md](CORPORATE.md) |
| Android APK | Companion for a paired Windows PC (not standalone) | [Android setup](../PARUX_ANDROID_README.md) |

## Install with the MSI

1. Download the MSI and `SHA256SUMS.txt` from the same release.
2. Verify the file (PowerShell):
   ```powershell
   Get-FileHash -Algorithm SHA256 .\PaRuX-*.msi
   Get-AuthenticodeSignature .\PaRuX-*.msi
   ```
   Compare the hash with `SHA256SUMS.txt`. The 4.1.0 MSI is signed with a **self-signed** certificate, so on another PC Windows may show
   "Unknown publisher" or a chain-trust failure (see [release record](../RELEASE_CANDIDATE_4.1.0.md)).
3. Run the MSI. The package is per-machine (installs under Program Files, with a Start Menu entry) and asks for administrator approval.
4. Start RBG – PaRuX from the Start Menu.

## First launch

Onboarding detects your hardware and what is already installed. Components follow **Detect, then Locate existing / Install with PaRuX / Skip**.
Nothing is downloaded without your confirmation. Choose your purpose (AI, Gaming, or both); you can change it later in Settings.
Then see [model setup in the README](../../README.md#set-up-a-model).

## Where your data lives

- MSI: `%LOCALAPPDATA%\PaRuX` (override with the `PARUX_DATA_ROOT` environment variable, which must be an absolute path).
- Portable and Corporate packages: `PaRuXData` next to the executable.
- Microsoft Store (MSIX) edition: the per-package `LocalState` folder.

Back up before uninstalling or changing machines. See [PRIVACY.md](PRIVACY.md).

## Update

Open **Updates** (shown in the Advanced interface). PaRuX checks the release metadata, a signed envelope and the MSI SHA-256, then offers
**Download & install verified MSI**. Installation is done by an elevated helper and only for a signed, strictly newer release.
Details: [WINDOWS_MSI_RELEASE.md](../WINDOWS_MSI_RELEASE.md). Portable packages have no in-app updater: replace the application files and keep `PaRuXData`.

## Uninstall

Use Windows Settings, Apps. Whether your data folder is kept or removed was not verified for the MSI; back up first.

## Known limits

- MSI install, upgrade from 4.0.1 and uninstall were **NOT RUN** on a clean PC for the 4.1.0 record; only static checks and a signature check were done.
- Another PC cannot verify the self-signed signature without trusting the certificate.
- No clean-machine or VM install test exists yet. See [RELEASE_READINESS.md](../RELEASE_READINESS.md).

Problems? See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) and [FAQ.md](FAQ.md).
