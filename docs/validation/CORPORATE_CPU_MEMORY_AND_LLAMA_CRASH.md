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

## Adaptive Corporate RAM policy (source-only, unverified)

On every CPU text-model transition, the existing PaRuX-owned outgoing text
runtime is stopped (not external Windows processes) and OS RAM is sampled again.
The memory planner estimates the SAME selected GGUF with **4096, 3072 and
2048 tokens**, accepting the largest context that fits within both the OS RAM
headroom and the **unchanged user/profile maximum total RAM percentage**.
GGUF metadata is read once per decision; AUTO uses the same policy while
ranking candidates, then the actual process launch repeats the calculation
with a fresh OS snapshot. An old saved/manual choice is never silently
replaced by a lower-quality model.

When less than 768 MiB of permitted headroom remains after the model estimate,
the bundled llama-server is requested to release its own idle model after
120 seconds instead of 300 seconds; for 768-1535 MiB spare, after 180 seconds.
With at least 1536 MiB spare the usual 300 seconds remains. These are
**llama.cpp idle release requests**, not proof of physical memory returned
or instant trimming when another program starts. No RAM is reserved or created,
and Windows/EDR/Outlook/Teams are untouched.

A **2048-token** context may reduce document chunk coverage and Chat/RAG
quality. PaRuX must tell the user when a reduced context was selected.
When the 2048-token estimate is still above available headroom (or OS RAM
cannot be measured), admission refuses safely rather than paging aggressively,
starting a crash loop, expanding the cap or degrading to an unverified 1024-token
context. The optional packaged 0.6B model remains a separate user-selected
build profile, not an automatic replacement.

### Validation additions

- Focused xUnit: `CorporateCpuMemoryPlannerTests`, including 16 GiB synthetic
  pressure at 4096/3072/2048 tokens, no-fit, unknown RAM, owned-model
  reclamation credit and adaptive idle thresholds.
- Run the real Corporate Lite 4B build under 16 GB pressure with and without
  Teams/Outlook. Record OS available memory, allowed profile RAM, selected
  context, loaded model, idle unload request, first-token time and **actual**
  peak RSS/private bytes. Confirm the same model remains selected.
- Test long synthetic corporate PDFs and large prompts at 3072 and 2048 tokens:
  reduced context must not be marketed as unchanged RAG quality. Verify
  short prompts remain useful, and the app reports context overflow honestly.
- Test two concurrent Chat requests, model switch, a resident runtime failing to
  exit and memory change between preflight and native start. No external
  process terminations or uncontrolled retries.
- Do not label this DONE until the Windows Release build, focused/full xUnit,
  FlaUI, offline real-model test and independent resource review pass.

## Conditional retrieval-memory reclamation (source-only, Windows evidence pending)

The low-memory Corporate path now attempts one additional **PaRuX-owned
memory-recovery pass** when even an adaptive context cannot fit:

1. The normal CPU planner first calculates available Windows RAM and
   configured profile headroom and tries the SAME selected GGUF at 4K/3K/2K.
2. Only if no context fits, PaRuX asks its existing Knowledge service to
   dispose idle in-process ONNX **embedding/reranker** sessions. In-progress
   index jobs or ONNX inference are not unloaded; no document/index data is
   removed. Non-Corporate editions do not use this path.
3. After a bounded 150 ms observation interval, PaRuX re-reads Windows RAM.
   If the OS really reports more admissible headroom, the same GGUF is tried
   again. This recovery pass is attempted at most once; no process kill,
   forced GC, global working-set trim, paging-policy change or automatic
   model substitution.
4. Chat AUTO has the corresponding one-pass recovery **before** refusing
   an empty compatible-candidate pool; otherwise it would reject everything
   before the final model-start recovery could run.
5. Both ONNX engines continue to use their existing lazy-load semantics,
   and the reranker now participates in the Knowledge idle timer. Corporate
   status reads do **not** accidentally preload an idle model.

Runtime status now exposes three optional fields (without changes to existing
fields): `ramAvailableBytes`, `corporateSystemRamLimitPercent`,
`corporateAdmissibleBytes`. The latter is **additional currently allowed
headroom without stopping any resident process**, not an amount promised for
a particular model. Runtime Manager displays the observed values
separately, rather than treating 16 GB installed as 16 GB available.

**Important limitation:** closing a PaRuX-owned ONNX session does not
guarantee Windows will immediately report a corresponding increase in
available physical RAM. A 4B GGUF may still not fit, and reducing the
context may degrade RAG quality. This feature does not dynamically shrink
an *already-running* llama-server mid-answer, and does not pretend to do so.

### Additional Windows acceptance gates

- With installed MiniLM embedding/reranker ONNX models, load then let them
  become idle; verify the reranker and embedder truly unload native memory,
  and a subsequent search reloads them with identical embeddings/scores.
- With one 16 GB standard-user Corporate PC and Teams/Outlook running,
  trigger a genuine low-headroom refusal with the same selected 4B GGUF,
  then let both ONNX models idle. Record before/after Windows available
  RAM, PaRuX private bytes, model context chosen and genuine first-token
  success. Do not count a simulated snapshot as an actual recovery.
- During large-PDF indexing, parallel search and concurrent RAG calls,
  ensure the recovery path refuses to unload active sessions or corrupt
  index state; after indexing stops, confirm idle unload is allowed again.
- Verify GET runtime status, hardware status and Runtime Manager (IT/EN)
  never load a reclaimed engine just to show a status; check that
  `ramAvailableBytes` can be null when measurement is missing.
- Run focused `CorporateIdleRetrievalReclamationTests`,
  `RerankerServiceTests`, `EmbeddingServiceTests`,
  `RuntimeStatusHardwareShapeTests`, `CorporateCpuMemoryPlannerTests`,
  full Server xUnit, Windows UI tests and real Corporate 8/16 GB offline
  evidence before merging.

## Local acceptance commands (Windows x64 build machine)

```powershell
dotnet build PaRuX.sln -c Release -p:Platform=x64
dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --filter "FullyQualifiedName~RuntimeLaunchFailureDiagnosisTests|FullyQualifiedName~RuntimeRecoveryGuidanceTests|FullyQualifiedName~SecurityMediumM123Tests|FullyQualifiedName~CorporateAutoMemoryRoutingTests|FullyQualifiedName~CorporateCpuMemoryPlannerTests|FullyQualifiedName~CorporateCpuWiringSourceContractTests"
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
