# Corporate CPU: memory admission and llama-server startup regression

**Status: source changes in review; full Windows / real-model acceptance NOT RUN.**
This document is a validation recipe, not a claim that Corporate Lite works on every 8–16 GB PC.
Never put company documents, actual machine/domain names, tokens, private logs or EDR settings in public issues.

## Reproduced classes of failure

1. Chat AUTO selects a CPU GGUF requiring about 2.9 GB, but the runtime's **effective**
   admission headroom is 0.8–2.1 GB. Windows may report more *available* physical RAM
   than PaRuX permits because the execution profile caps **total system RAM use including
   other applications**. This is not automatically a Windows memory leak.
2. After the user closes competing applications and retries, llama-server exits during
   initialization and the early-exit 503 contains only the beginning of stderr. The real
   cause is **unknown** without the complete tail and exit code. Security controls,
   binary compatibility and remaining memory pressure must be investigated separately.

## Source-level changes covered in this patch

- Keep the CPU admission guard and per-profile ceiling; do not terminate external processes
  or silently expand RAM limits.
- State separately: needed model RAM, current admissible RAM, Windows available RAM and
  the applied profile ceiling. The OS value is not a promise that the model can load.
- Wait *at most two seconds* for an already-exited child's stderr drain so the final
  allocator/loader diagnostic is less likely to be lost.
- Include the child's hexadecimal exit code and classify explicit Windows OOM status /
  llama.cpp allocation failures. Unknown exits remain unknown.
- Map recognized CPU memory refusals and llama-server exits to localized recovery text
  instead of presenting raw JSON/paths/stdout/stderr in a Chat bubble.
- Corporate Chat AUTO now samples live RAM for its candidate ranking pass and uses the
  same effective budget formula as CPU launch (including **only verified PaRuX-owned**
  resident working-set credit). Unmeasurable and oversized candidates do not pass
  automatic selection; exact/manual model requests remain separate.
- The final Corporate text transition uses the CPU/4K GGUF footprint instead of
  the generic GPU/32K estimate. Corporate arbitrary and fresh-exact role paths
  invoke the bundled CPU engine rather than selecting CUDA/Vulkan after ranking.
  LoRA adapters are refused in this CPU-only path.

## Separate low-memory Corporate package candidate (source only)

Corporate packaging has a **separate opt-in build switch** for Qwen3-0.6B Q8_0
from the official Qwen Apache-2.0 repository. The exact weight file SHA-256
is checked before packaging. **No Qwen weights are included in Git and no
new ZIP or release was produced in this session.**

On the trusted Windows build machine (never the corporate target PC):

```powershell
pwsh -File scripts/build-corporate-lite.ps1 -Fetch -LowMemoryStarter -RequireReranker -Smoke
```

This switch writes into `artifacts/corporate-lite-low-memory/` with a distinct
`PaRuX-Corporate-Lite-LowMemory-Offline-win-x64.zip`; the existing
standard Corporate 4B archive in `artifacts/corporate-lite/` is not replaced.

The generated manifest identifies `starterProfile=low-memory-0.6b`, the
source and declared licence, and every packaged byte. A smaller model uses
less RAM for weights but may still exceed the live execution-profile limit
due to KV cache and OS workload; its QA/RAG quality is materially weaker
than the default 4B. **Do not silently substitute it for a manually selected
model or describe it as verified on 8 GB systems.**

The builder also accepts `-ModelPath` for an explicit local GGUF. Its
README and manifest now reflect the *actual* model filename, and custom
origins/licences are marked unverified unless the builder supplies metadata.
The package-builder's origin/label is not independent legal clearance.

## Local acceptance commands (Windows x64 build machine)

```powershell
dotnet build PaRuX.sln -c Release -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~RuntimeLaunchFailureDiagnosisTests|FullyQualifiedName~RuntimeRecoveryGuidanceTests|FullyQualifiedName~SecurityMediumM123Tests|FullyQualifiedName~CorporateAutoMemoryRoutingTests|FullyQualifiedName~CorporateCpuWiringSourceContractTests"
# Then the full Server xUnit suite and exclusive FlaUI regression, in that order.
```

No GitHub Actions, unattended signing, publishing, tagging or release. Check the build target
and all actual test names against the checkout before recording results.

## Real corporate test matrix (synthetic prompts/documents only)

| Host / workload | Required outcome |
|---|---|
| 16 GB CPU-only; OS usage around 60–65% before PaRuX | Chat AUTO respects current admission and never kills Outlook/Teams/EDR |
| 16 GB CPU-only; background collaboration tools active | Safe refusal before spawn when the selected model cannot fit; intelligible suggestion |
| 8 GB CPU-only low-memory test | No model-start crash loops, fabricated success, automatic downloads or unsafe RAM-cap override |
| 16–32 GB CPU-only sufficient headroom | Bundled GGUF loads and responds to a *real* prompt, no stale runtime process |
| Corporate AUTO and manually locked model | AUTO uses live RAM budget; manual choice stays exact and does not silently substitute |
| Corporate role, arbitrary candidate and fresh-exact load | All use bundled CPU llama-server, 4K context estimate, no CUDA/Vulkan launch |
| Explicit incompatible llama binary | Exit code + redacted stderr preserved locally, helpful CPU-variant guidance |
| Deliberately broken GGUF or unavailable model | Correct distinct message, no accidental fallback to the wrong saved/explicit model |
| Unexpected crash with incomplete stderr | Correct unknown-cause message with diagnostic route; no speculation that it was OOM |
| Corporate offline + blocked app control | Does not attempt cloud fallback, bypass DLP/EDR, or silently alter system policy |

Record in a **local, private** test report: app/build SHA, package flavor, total/available RAM,
effective profile RAM cap, model path **redacted** and GGUF basename/quantization, context,
runtime binary variant, exact exit code, redacted stderr tail, model load latency, working set,
and whether first-token response was generated. Do not commit device identifiers or confidential
log contents.

## Still required before calling this fixed

- **P0:** Perform real offline 16 GB and 8 GB test runs under memory pressure. Confirm
  recovery and user experience; these cannot be inferred from unit tests.
- **P0:** Exercise new source-wired Chat AUTO live fit, capability routing, recommended
  winners, strict RAM refusal and final CPU-only launch on real Windows with native models;
  source tests alone cannot prove the feature works.
- **P0:** Offer an explicit user-confirmed smaller-model option when no fitting installed
  candidate exists; current code refuses safely, it does **not** add an interactive model
  picker or silently download a different model.
- **P0:** Validate Corporate Lite package with a suitable light GGUF for 8–16 GB machines,
  respecting model licensing, offline operation and predictable quality; don't force a 4B starter
  when it will not fit.
- **P1:** Expose a local diagnostics panel: OS free memory vs configured profile headroom,
  current model and context, exit code, redacted last error and safe retry guidance.
- **P1:** Measure cold start, first token, recovery, process lifetime, and RAG ingestion with
  normal corporate background load; no performance claims without before/after evidence.
- **P1:** Include native dependency and Windows/AVX compatibility checks without disabling
  organizational endpoint protection.
- **P2:** Test bounded retry/quarantine policies, context scaling where explicitly permitted,
  and recovery when memory fluctuates while the model is loading.

A typed error is not the same as a functional model. Completion requires a clean packaged
Corporate Lite on a standard-user Windows profile, a real response, full regression and
independent review. No test above has been run by editing source via the GitHub connector.
