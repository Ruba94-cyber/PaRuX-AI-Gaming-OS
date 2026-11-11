# Microsoft Store submission checklist (owner steps)

Nothing here has been submitted. Items marked OWNER need the owner's Partner Center account or machine. Status legend: DONE / TODO / NOT RUN.

## A. Account and identity (OWNER)

1. DONE (owner) Register a free developer account (individual) at https://storedeveloper.microsoft.com (Partner Center).
2. DONE (owner) Reserve the app name. The build default display name is "PaRuX AI & Gaming OS"; if you reserve a different name, put it in the identity file as `appDisplayName`.
3. DONE (owner, 2026-11-11) In Partner Center open Product management > Product identity and copy **Package/Identity/Name**, **Package/Identity/Publisher** and **Package/Properties/PublisherDisplayName** into the Git-ignored file `<main checkout>\.ai\store\store-identity.local.json` (keys `identityName`, `identityPublisher`, `publisherDisplayName`; optional `appDisplayName`). Values are case- and punctuation-sensitive. Never commit this file (`*.local.json` is ignored).
4. TODO Pricing: Free. Markets: choose. Availability: public.

## B. Build the package (agent-reproducible)

```powershell
./scripts/build-msix.ps1 -IdentityFile "<main checkout>\.ai\store\store-identity.local.json" `
    -RuntimeCacheDir "<main checkout>\artifacts\cache\runtime"
```

Output: `artifacts\msix\PaRuX_<major.minor.patch>.0_x64.msix` (+ `.sha256.txt`). It is unsigned on purpose: the Store re-signs MSIX submissions. The Store requires version `x.y.z.0`; the script enforces it.

## C. Evidence still missing before submitting

| Check | Status | How |
|---|---|---|
| Windows App Certification Kit on the .msix | DONE 2026-11-11: overall PASS, 2 optional tests fail (WebView2 metadata, blocked executables) - QA-044. Re-run after any packaging change (needs an elevated prompt) | In an elevated PowerShell: `& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset` then `appcert.exe test -appxpackagepath <msix> -reportoutputpath wack-report.xml`. Keep the XML report; fix every failure. |
| Local install + first run of the packaged app | DONE 2026-11-11 through the Core API (QA-043); the window and a chat with a downloaded model are still to be checked by eye (needs trust of a test certificate machine-wide, or Developer Mode) | Option 1: `./scripts/build-msix.ps1 -IdentityFile ... -SkipPublish -SignTestCopy`, then in an elevated PowerShell import the printed certificate's `.cer` into `Cert:\LocalMachine\TrustedPeople` and `Add-AppxPackage <TESTSIGNED.msix>`. Option 2: turn on Developer Mode and `Add-AppxPackage -Register <layout>\AppxManifest.xml`. Remove the certificate and package afterwards. |
| First-run checks once installed | NOT RUN | PaRuX starts; Core starts (`PaRuX.Server.exe` child of PaRuX.exe); Chat with a downloaded small model works; data appears under `%LOCALAPPDATA%\Packages\<PFN>\LocalState\PaRuX` and **not** under `%LOCALAPPDATA%\PaRuX`; Update Manager says the Store manages updates; app uninstalls cleanly. If the app fails to start, the first suspect is WinRT activation / `resources.pri` for a self-contained WinUI 3 app with package identity (see `PACKAGING_DECISION.md`). |
| Partner Center certification | NOT RUN | Upload the .msix on the Packages page. |

## D. Store listing (OWNER)

1. TODO Paste text from `LISTING_en.md` and `LISTING_it.md` (remove the evidence references in parentheses). Add Italian as a second listing language.
2. TODO Privacy policy URL: publish `PRIVACY_POLICY.md` at a public HTTPS address (for example a GitHub Pages page or the repository's rendered file). Required because the app accesses the internet and the network.
3. TODO Support contact: use `SUPPORT.md` content at a public URL or an e-mail address you choose.
4. TODO Age rating: complete the IARC questionnaire yourself (notes in `LISTING_en.md`).
5. TODO Category: Productivity (or Developer tools); decide.
6. TODO Store logos: Partner Center asks for a 1:1 300x300 store icon (the package already contains square tiles derived from the approved PRX Orbit artwork; export a 300x300 from `src/PaRuX.App/Assets/prx-orbit-approved.png`).

## E. Screenshots to capture (REAL, none are in the repo)

Capture on the reference PC with a model installed, from the packaged app, 1920x1080 (min 1366x768) PNG, English UI first, then Italian UI. Use only synthetic documents (no company or personal data; the repository is public).

1. Chat home with a streamed answer from a local model.
2. Document question: a synthetic folder indexed, answer with the source shown.
3. A refusal when the answer is not in the documents.
4. Model Hub with a model list and the download confirmation.
5. First-run or model setup (Detect > Locate / Install / Skip).
6. Settings showing local-first/privacy options, with LAN pairing off.
7. Italian UI of screen 1.
8. Update Manager showing "managed by the Microsoft Store" (proves the self-updater is disabled).

## F. Restricted capability justification (paste into the `runFullTrust` field)

> PaRuX AI & Gaming OS is a desktop app (WinUI 3 / .NET) that runs AI language models locally on the user's PC. The app starts its own local server process (PaRuX.Server.exe) and the local inference engine (llama.cpp, MIT license) as child processes, reads and writes files the user chooses (documents to index, model files), and uses native GPU libraries (Vulkan) that cannot run inside a UWP/AppContainer sandbox. It therefore needs the runFullTrust capability. It does not install services or drivers, does not run elevated, does not change Windows settings, registers no startup entries, and does not collect telemetry. Network use is user-initiated: model downloads, optional web search, and an optional local-network pairing feature that is off by default.

Network capabilities declared: `internetClient`, `internetClientServer`, `privateNetworkClientServer` (justify the last two as the optional local-network pairing listener).

## G. Notes for certification (paste in Partner Center)

See the "Notes for certification" section of `LISTING_en.md`. Add: no sign-in is needed; the first chat requires a downloaded model (a small model is enough to test); test with a synthetic text file for the document feature.

## H. Policy risks to decide before or after the first certification result

1. **Executable downloads (policies 10.2.2, 10.2.3, 10.2.4): RESOLVED in code.** The Store edition (package identity, or `PARUX_STORE_EDITION=1` set by the packaged app on the Core) never downloads or installs executable code: no additional inference runtimes or engines, no managed Python/pip tooling, no ComfyUI/media engines, no game archives or installers. The Core refuses those routes with HTTP 403 and "Not available in the Microsoft Store edition" before any network or disk activity (`StoreEditionPolicy`, `StoreEditionGuardMiddleware`), and the UI hides or disables them. The baseline engine ships in the package; models and other pure data still download on user consent; "Locate existing" for software the user installed still works. Still to verify on the packaged install: the refusal and the hidden controls (see section C).
2. **Gaming area**: the embedded torrent engine and the external torrent-client adapter are unavailable in the Store edition (never started, no listening port), and HTTP(S) game-archive/installer downloads are disabled too. What remains is the game library, launching games already installed on the PC and store discovery. Decide whether that remaining surface is acceptable for a reviewer, or hide the Gaming workspace entirely.
3. **Open security findings** in `docs/SECURITY_REVIEW.md` (pairing guessing and token expiry, token accepted from URL query, runtime ports without API key, .NET 8 end of support) touch the optional LAN/pairing feature that the Store listing mentions. Fix or drop the claim before submitting.
4. **Beta status**: the repository calls 4.1.0 a beta and `docs/REQUIREMENT_MATRIX.md` shows most rows as not Release Ready. A Store listing should say "early release", as the drafts do.
5. **Data retention**: uninstall deletes downloaded models and chats (normal for MSIX). The listing and support page say so.
6. **Name/branding**: confirm the reserved name does not conflict with third-party trademarks (the app mentions llama.cpp, Hugging Face, Steam etc.; do not use them in the title).
