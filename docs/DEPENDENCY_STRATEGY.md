# PaRuX AI & Gaming OS — first-party dependency strategy

Status: 2026-11-01. This is a source-backed engineering policy, not proof of a release.

## Principle

Use original PaRuX C#/.NET/WinUI/Win32 code for orchestration, local libraries,
controller navigation, permissions, session state, UI, model selection, downloads
and recovery when the platform already provides the required primitives.

**Do not copy code from proprietary products, remove upstream license notices,
relabel third-party code as PaRuX-authored, bypass DRM, bundle unlicensed games,
or create unsigned/unsafe virtual-device drivers.** Permissively licensed source
can be reused only under its license with exact attribution, provenance and
reproducible source/version tracking. Product-level independence is not a
pretext to replace proven security-critical libraries with unreviewed copies.

## Verified source layout / current boundaries

| Capability | Preferred implementation | Remaining dependency / limitation |
| --- | --- | --- |
| Console/TV focus navigation | First-party `ConsoleNavigationPolicy`, WinUI-delivered gamepad keys (D-pad and left stick) | Windows/WinUI itself; physical controller acceptance not run |
| Standard XInput diagnostics | First-party native `XInputGetState` P/Invoke, on demand | Windows XInput runtime; diagnostics do not remap games |
| Game library and profiles | First-party Steam/Epic/GOG discovery, JSON stores and Gaming orchestrator | User-installed official game clients for launch/DRM; no embedded store software |
| Steam / GOG launch | Explicitly confirmed official-client handoffs | Client controls actual game; launch and ownership not verified on physical PC |
| Optional controller remapping | No remapper needed for PaRuX Console UI | Legacy XOutput optional; universal virtual XInput output still requires documented Windows driver/device ownership |
| ZIP extraction | First-party extraction policy with BCL `System.IO.Compression` | `SharpCompress` remains for 7z and other unsupported formats; no NuGet package removed yet |
| Local inference | PaRuX-owned orchestration, routing, workers and managed processes | Actual GGUF/ONNX/ComfyUI/PyTorch backends remain specialized third-party components |
| Model conversion | PaRuX workflow wrapping vendored llama.cpp MIT conversion subset | Retain exact upstream MIT notices and origin; cannot claim this code is original |
| Security/storage | First-party authorization and operation policy, platform DPAPI, SQLite | Crypto, database and OS interfaces should not be reimplemented without strong evidence |

## New dependency gate

- `packaging/dependency-boundaries.json` records **19 direct references across App + Server, 15 distinct NuGet packages** at this checkpoint.
- `tools/audit-dependency-boundaries.ps1` is a read-only, offline PowerShell/.NET check. It requires no Python, npm, GitHub Actions or network. New/removed/version-changed packages fail pending an explicit manifest review; the vendored llama.cpp license file must remain present.
- `tools/test-gos-checkpoint.ps1` runs this gate before its Windows builds. This is **not** a vulnerability scanner, a dependency-tree/SBOM transitive check, license validation of every NuGet package, a build result or a performance benchmark.
- Update `THIRD_PARTY_NOTICES.md`, `OPEN_SOURCE_PROVENANCE.md`, lock/provenance assets and release evidence whenever an external component is added, replaced or redistributed.

## Priority order for eliminating optional runtime dependencies

1. Complete native, controller-first Console navigation, game library and session lifecycle with existing WinUI events. Keep no resident gamepad polling/voice worker during games.
2. Implement independently controlled, signed/official-store link and download transactions with explicit owner consent. Do not enable untrusted installer execution or game repacks as trusted by default.
3. Add reversible, capability-checked display profiles using Windows native APIs and crash-recovery journals, without changing HDR/refresh unless the OS reports support.
4. Provide optional controller mapping only with explicit devices/ownership and proven deduplication. A user-mode C# library cannot honestly emulate a complete Xbox controller for arbitrary games without an appropriate kernel/device backend.
5. Keep heavy AI backend engines as isolated, optional runtimes until a *measured* first-party substitute actually matches quality, GPU support, compatibility and maintenance requirements.
6. Reduce/replace individual NuGet packages only after call-site inventory, fixture parity, fail/recovery tests, performance comparison and licensing review. Avoid a large dependency purge that breaks existing features.

## Acceptance and truthfulness

`SOURCE IMPLEMENTED` means code exists, not that it builds or runs. Windows
Release builds, focused xUnit, WinUI/FlaUI, native controller/display/Steam/GOG
handoffs, NVIDIA/AMD/CPU benchmarks, restart recovery, accessibility and
security review must be recorded before a track becomes `WINDOWS VERIFIED`.
Do not state that only physical PC testing remains while the canonical plan
still lists unfinished source development.
