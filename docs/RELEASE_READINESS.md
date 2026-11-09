# Release readiness

**Verdict: NOT RELEASE READY.** Last published tag: `v4.0.1`. Target of this cycle: `4.1.0` (release candidate `4.1.0-rc1`, local only). Evidence: [QA_EVIDENCE.md](QA_EVIDENCE.md). Nothing is tagged, signed, packaged or published without the owner's explicit approval.

Updated 2026-11-10 at `031baa84`.

| Gate | Status | Basis |
|---|---|---|
| 1 Feature completeness | PARTIAL | 17 plan sections are SOURCE PARTIAL (written remotely, unit-tested only); per-section native checks are listed in `.ai/artifacts/native-verification-matrix.md` (local). Gaming parity gaps GAM-003..007 open |
| 2 UX/UI | PARTIAL | Real-app screenshots reviewed at 200% on 2560x1600 only; scaling 100/125/150%, 4K, TV, multi-monitor not tested (TV/second monitor need the owner's hardware) |
| 3 Stability | PARTIAL | P0 fixed: Core start when Windows reserves port 51123; onboarding profile switch blocked by detection fixed (QA-016) |
| 4 Performance | NOT RUN | No AI/FPS/startup benchmarks executed this cycle |
| 5 Security | PARTIAL | Static review done (QA-017): 0 Critical / 0 High / 7 Medium; none remediated; `dotnet list package --vulnerable` not run |
| 6 Testing | PARTIAL | Server xUnit 13,036 pass / 0 fail (QA-014). FlaUI Regression 273/308 (QA-015), 35 failures under triage |
| 7 Packaging (MSI, Portable, Portable Corporate, APK) | NOT RUN | |
| 8 Signing | NOT RUN | Signing material located (self-signed code-signing PFX, Android release keystore, release-manifest key) in the owner's Documents folders; owner authorised its use; nothing signed this cycle |
| 9 Documentation | PARTIAL | QA evidence, security review and this file current; README/feature matrix/release notes not refreshed |
| 10 Public readiness | NOT RUN | No screenshots or benchmark claims prepared; none may be asserted without evidence |

## Blocked on the owner's hardware
- TV 4K, second monitor, physical controllers, eGPU disconnect/reconnect, a low-spec PC for the Corporate profile, a physical Android device.

## Authorizations still needed
- Final approval before any tag push, GitHub release or distribution of installers.
- Confirmation of the model bundled in Portable Corporate (must be Apache/MIT licensed) before it is included.
