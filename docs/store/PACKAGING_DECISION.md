# Microsoft Store packaging decision

Date: 2026-11-11. Status: decision taken, package builds; install, WACK and Partner Center certification are NOT RUN (see `SUBMISSION_CHECKLIST.md`).

## Decision

Ship the Store edition as an **MSIX full-trust packaged desktop app** (`runFullTrust`), built by `scripts/build-msix.ps1`. Do **not** use the Store "EXE/MSI" submission path. The existing unpackaged MSI and Portable ZIPs are unchanged and stay the direct-download editions.

## Why not EXE/MSI submission

| Question | MSIX | EXE/MSI |
|---|---|---|
| Signing | Not required for submission; the Store re-signs the package with a Microsoft certificate. | The installer and **all PE files** must be signed by a CA in the Microsoft Trusted Root Program; self-signed is not accepted (policy 10.2.9). |
| Owner's situation (budget 0, self-signed certificate only) | Possible. | Blocked until a paid CA certificate exists. |
| Updates | Store-managed, differential. | The publisher must host a new versioned HTTPS URL for every update. |
| Uninstall | Clean, handled by the OS (policy 10.2.7). | Depends on our MSI. |

The MSI path would also require Authenticode-signing every shipped DLL/EXE (hundreds of files in a self-contained .NET + Windows App SDK app) with a certificate the owner does not have. MSIX needs none.

Sources:
- Code signing options (MSIX is re-signed by the Store; MSI/EXE needs a Trusted Root CA, self-signed not accepted): https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options
- App package requirements for MSIX (Store replaces signatures; no CA certificate or pfx needed): https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements
- Choose a distribution path (MSI/EXE: Trusted Root CA, no Store-managed updates): https://learn.microsoft.com/windows/apps/package-and-deploy/choose-distribution-path
- Microsoft Store Policies 10.2.9 (installer submission requirements): https://learn.microsoft.com/windows/apps/publish/store-policies

## Store policy review for PaRuX's behaviour

| Behaviour | Policy / rule | How the Store edition handles it |
|---|---|---|
| Full-trust Win32/.NET app that launches the local Core and inference engines | `runFullTrust` is a **restricted capability**; Partner Center asks for a justification. https://learn.microsoft.com/windows/apps/package-and-deploy/app-capability-declarations | Declared; justification text is in `SUBMISSION_CHECKLIST.md`. |
| Downloads AI models on user consent | Data, not code; allowed. Must be disclosed in the listing/privacy policy (10.5). | Model Hub downloads only on explicit user action. Disclosed in `PRIVACY_POLICY.md` and the listings. |
| Downloads **optional runtimes / executables** on consent (extra llama.cpp builds, managed Python tooling, media engines) | 10.2.2: must not change or extend described functionality through dynamic inclusion of code; 10.2.3: must not install secondary software not developed by you that does not enhance the product; 10.2.4: disclose non-integrated dependencies at the start of the description. https://learn.microsoft.com/windows/apps/publish/store-policies | **Main certification risk.** Mitigations: the baseline Vulkan llama.cpp runtime ships *inside* the package (hash-locked, same staging as the MSI) so base Chat needs no executable download; remaining downloads are optional, user-initiated, described in the listing, and each enhances a disclosed feature. If certification objects, the fallback is to disable executable-runtime downloads in the packaged edition (not implemented; decision for the owner after the first certification result). |
| Self-updater | Store owns updates; a parallel MSI updater would create a second install. | `PackageIdentity.AllowsInAppSelfUpdate` is false for any process with package identity: the Update Manager's PaRuX Application section shows "managed by the Microsoft Store", makes no network call and offers no download; the download method refuses to run. `PaRuX.UpdateHelper` is not in the package. Runtime/component/model update review in the Update Manager is unchanged. |
| Optional LAN server and device pairing | Network capabilities should be the minimum needed (10.6 Capabilities). | `internetClient` (downloads, optional web access), `internetClientServer` + `privateNetworkClientServer` (the optional HTTPS LAN listener, only opened when the user enables remote access; Core's own API stays on loopback). A full-trust process is not an AppContainer, so these declarations are informational for the Store and review. |
| Uninstall | 10.2.7: clean uninstall must be possible. | MSIX removes the app; the user data folder (below) is removed with the package. |
| Cryptocurrency mining, changing Windows settings, startup entries | 10.2.6, 10.2.8 | Not applicable; no startup task, no protocol or file association is registered. |

## Behaviour changes with package identity (audit result)

| Area | Finding | Action |
|---|---|---|
| Data root | A packaged process has writes under `%LOCALAPPDATA%` redirected to a private per-package copy while existing real files are still opened in place; this could mix with an unpackaged MSI install's `%LOCALAPPDATA%\PaRuX`. https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes | `PackageIdentity` (runtime `GetCurrentPackageFamilyName`, not a compile constant) makes `PaRuXDataRoot` and `PaRuXLocalAiStateRoot` resolve to the real, unvirtualized `%LOCALAPPDATA%\Packages\<PFN>\LocalState\PaRuX` (and `\LocalAI`). Desktop, Core and child processes use the same Contracts code, so they agree. Order is unchanged: Portable marker, then `PARUX_DATA_ROOT`, then package, then the legacy path. Two Gaming stores that hard-coded `%LOCALAPPDATA%\PaRuX\server` now use `PaRuXDataRoot` (identical path for MSI installs). |
| Existing MSI user switching to the Store build | Their chats/models stay in `%LOCALAPPDATA%\PaRuX`; the Store build starts with its own folder. | No automatic import (never touches USER data silently). Use PaRuX's Backup/Restore. Documented in the checklist and support page. |
| Data retention on uninstall | MSIX deletes `LocalState` on uninstall, including large downloaded models. | Accepted Store behaviour. The listing and support page tell users to use Backup first. |
| Single instance | Named mutex works unchanged. | None. |
| Tray icon | H.NotifyIcon works in packaged apps. | None (not exercised here). |
| Startup task, protocol, file associations | None exist in the code. | None declared. |
| Child processes | Core and engines run from the read-only package directory (allowed for full trust). The baseline runtime is copied into the data root before use, never run-and-write inside the package. | None. |
| Resource lookup | A packaged WinUI app resolves XBF/assets through `resources.pri` named after the package identity. | `build-msix.ps1` merges the build's PRI files into that file with `makepri`. |

## Known gaps in this evidence

- The package has **not** been installed or started: Developer Mode is off and sideloading a signed copy needs an elevated certificate-trust step. First-run behaviour of a self-contained WinUI 3 app with package identity (activatable-class registration through the executable's embedded manifest, `resources.pri`) is therefore unverified.
- The Windows App Certification Kit needs elevation: NOT RUN.
- Partner Center certification (including the policy risks above) has not been attempted.
