# Dependency, licence and SBOM audit - 2026-11-11

Scope: `PaRuX.sln` (NuGet, .NET 8 at audit time; now .NET 10) and `src/PaRuX.Android` (Gradle). This is an inventory and documentation check. It is not legal clearance and not a full transitive licence review. Base commit: `18fe4a67`.

## 1. Commands and results

| # | Command | Result | Notes |
|---|---|---|---|
| 0 | `dotnet restore PaRuX.sln` | PASS | Required before `dotnet list`; no source changed |
| 1 | `dotnet list PaRuX.sln package --vulnerable --include-transitive` | PASS | "no vulnerable packages" for all 9 projects, queried against api.nuget.org |
| 2 | `dotnet list PaRuX.sln package --deprecated` | PASS, 1 deprecation (test-only) | `xunit` 2.9.3 flagged Legacy in `PaRuX.Server.Tests` and `PaRuX.UITests`; not shipped |
| 3 | `pwsh -NoProfile -File scripts/generate-sbom.ps1` | PASS | Scope `all`; 238 components; see section 4 |
| 4 | `pwsh -NoProfile -File tools/validate-license-inventory.ps1` | PASS | 4 lock artifacts, 26 MSI notice rows checked (re-run after the edits in section 3) |
| 5 | `pwsh -NoProfile -File tools/check-direct-third-party-coverage.ps1` | PASS | All 24 direct PackageReference entries are mentioned in the notices |
| 6 | `pwsh -NoProfile -File tools/audit-dependency-boundaries.ps1` | FAIL | 3 deviations: `MonoTorrent`, `PdfPig`, `System.Drawing.Common` are not in `packaging/dependency-boundaries.json` (see section 3, item B6) |
| 7 | `pwsh -NoProfile -File tools/check-public-safety.ps1` | PASS | `public-safety: OK` after the edits |
| 8 | Gradle dependency vulnerability scan | NOT RUN | No Gradle vulnerability task is configured in this repository |

## 2. Vulnerable and deprecated packages

- Vulnerable: none found in the resolved graph (transitive included) for the nine solution projects.
- Deprecated: `xunit` 2.9.3, reason "Legacy", suggested replacement `xunit.v3`. Severity: informational (maintenance, not a known vulnerability). Fixed version: not applicable, the migration is to xunit.v3. Scope: test projects only, not in the shipped MSI.

## 3. Licence gaps (file and line)

Status key: FIXED = corrected in this commit; OPEN = not changed, owner or source review needed.

| ID | Component | Gap | File and line | Status |
|---|---|---|---|---|
| B1 | Microsoft.WindowsAppSDK 1.8.260804001 | Notice said MIT. The package's `license.txt` and nuspec are Microsoft Software License Terms (not MIT). | `THIRD_PARTY_NOTICES.md:14` | FIXED |
| B2 | Qwen3-4B-Q4_K_M.gguf (starter chat model, in Portable and Corporate Lite) | No notice row. Script states Apache-2.0, upstream not verified. | `scripts/build-corporate-lite.ps1:174`, `scripts/portable-offline-assets.ps1:137`; notice `THIRD_PARTY_NOTICES.md:48` | FIXED as UNKNOWN - NEEDS REVIEW |
| B3 | all-MiniLM-L6-v2 (embedding ONNX) | No notice row. Script states Apache-2.0, upstream not verified. | `scripts/build-corporate-lite.ps1:179-181`, `scripts/portable-offline-assets.ps1:142`; notice `:49` | FIXED as UNKNOWN - NEEDS REVIEW |
| B4 | Xenova/ms-marco-MiniLM-L-6-v2 (reranker ONNX) | No notice row. Script itself says review the source licence before redistribution. | `scripts/build-corporate-lite.ps1:189-191`, `scripts/portable-offline-assets.ps1:147`; notice `:50` | FIXED as UNKNOWN - NEEDS REVIEW |
| B5 | llama.cpp CPU x64 runtime in Portable and Corporate Lite | Fetched from the latest release, not version- or hash-pinned, so the notice cannot name a build. | `scripts/portable-offline-assets.ps1:108-113`, `scripts/build-corporate-lite.ps1:145-148`; notice `:51` | FIXED as a documented gap (MIT upstream stated, build not pinned) |
| B6 | MonoTorrent 3.0.2, PdfPig 0.1.16, System.Drawing.Common | Licences are known and already in the notices (MIT from LICENSE; Apache-2.0 from nuspec; MIT from nuspec). They fail only the dependency-boundary baseline. Updating the baseline needs owner review, so it was not changed. | `src/PaRuX.Server/PaRuX.Server.csproj:45,65,67`; baseline `packaging/dependency-boundaries.json` | OPEN |
| B7 | System.Drawing.Common version split | Resolved by the .NET 10 migration: App and Server now both pin 10.0.15 (previously App 8.0.10, Server 8.0.0). Both MIT, but the versions differ. | `src/PaRuX.App/PaRuX.App.csproj:55`, `src/PaRuX.Server/PaRuX.Server.csproj:67` | OPEN (version alignment, not a licence issue) |
| B8 | Kotlin stdlib (transitive, APK) | Not in the notices. Cached JAR has no licence file and the POM is not cached, so the licence cannot be proven here. | notice `THIRD_PARTY_NOTICES.md:87` | FIXED as UNKNOWN - NEEDS REVIEW |
| B9 | okio-jvm (transitive of OkHttp, APK) | Same as B8. | notice `:88` | FIXED as UNKNOWN - NEEDS REVIEW |
| B10 | Gradle components generally | SBOM has 150 maven components. Most carry no licence field, so the APK is covered only by family wording in the notices. | SBOM, section 4 | OPEN |

Copyleft and non-commercial items (none redistributed in the MSI, Portable or Corporate Lite ZIP as far as the scripts show):

- SearXNG, AGPL-3.0-or-later, user-operated endpoint only: `THIRD_PARTY_NOTICES.md:61`.
- Odysseus, AGPL-3.0-or-later, reference only: `THIRD_PARTY_NOTICES.md:64`.
- ComfyUI, GPL-3.0, separate process, not in the MSI: `THIRD_PARTY_NOTICES.md:100`.
- Proprietary redistribution terms (not copyleft): Windows ML `:27`, Visual C++ runtime `:36`, NVIDIA CUDA EULA `:37` and `:99`, WiX OSMF `:119` (build-time only).
- Non-commercial: no non-commercial model or component is bundled. The catalogue flags non-commercial Qwen-Image bundles, which are not shipped (`docs/compliance/THIRD_PARTY_COMPONENTS.md:20`).

Items that remain UNKNOWN - NEEDS REVIEW: B2, B3, B4, B8, B9. Owner review is required before the Portable or Corporate Lite ZIPs are published with these models.

## 4. SBOM

- File: `artifacts/sbom/SBOM.all.cyclonedx.json` (git-ignored by `.gitignore:12`, not committed).
- Format: CycloneDX 1.5 JSON, generated by `scripts/generate-sbom.ps1` (default scope `all`, covering the .NET publish graph and the Android Gradle graph).
- Content: 238 components, 83 NuGet (`pkg:nuget`), 150 Maven (`pkg:maven`), 5 without a purl.
- Licences: 152 components have no licence field in the document. The generator reports 112 licences resolved from local package caches. This is why B10 is open.

## 5. Changes in this commit

- `THIRD_PARTY_NOTICES.md`: corrected the WindowsAppSDK licence (B1); added a Portable and Corporate Lite section for the models and the CPU runtime (B2-B5); added Kotlin stdlib and okio-jvm as UNKNOWN - NEEDS REVIEW (B8-B9).
- `docs/compliance/DEPENDENCY_AUDIT_2026-11-11.md`: this report.
- No source code, no lock or baseline files, and no `packaging/dependency-boundaries.json` changes.

## 6. Not done

- Upstream licences for the models (B2-B4) were not read. No model card or licence file is in the repository or the local caches.
- Gradle vulnerability scan (row 8) was not run.
- The dependency-boundary baseline (B6) is still failing and needs an owner decision.
