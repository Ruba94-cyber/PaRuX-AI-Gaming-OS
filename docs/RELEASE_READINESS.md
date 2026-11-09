# Release readiness

**Verdict: NOT RELEASE READY.** Last published tag: `v4.0.1`. Evidence: [QA_EVIDENCE.md](QA_EVIDENCE.md). Nothing is tagged, signed, packaged or published by this work without the owner's explicit approval.

| Gate | Status | Basis |
|---|---|---|
| 1 Feature completeness | PARTIAL | ~20 plan sections still SOURCE PARTIAL / not natively verified; Gaming parity gaps listed in the plan (task GAM-*) |
| 2 UX/UI | PARTIAL | Real-app screenshots reviewed at 200%; Gaming home compacted; scaling 100/125/150%, 4K, TV, multi-monitor not tested |
| 3 Stability | PARTIAL | P0 found and fixed: Core failed to start when Windows reserves port 51123 |
| 4 Performance | NOT RUN | No AI/FPS/startup benchmarks executed this cycle |
| 5 Security | NOT RUN | No audit this cycle |
| 6 Testing | PARTIAL | xUnit 13,0xx pass; FlaUI Smoke 35/35; Regression 289/308 before the latest fixes |
| 7 Packaging (MSI, Portable, Portable Corporate, APK) | NOT RUN | |
| 8 Signing | NOT RUN | signing folders not found at the documented paths (`Documents\local ai`, `Documents\firmapp`); location must be confirmed by the owner |
| 9 Documentation | PARTIAL | this file, QA_EVIDENCE.md and the plan entry created |
| 10 Public readiness | NOT RUN | no screenshots/landing/benchmark claims prepared; none may be asserted without evidence |

## Authorizations still needed
- Location of the signing material (certificate, Android keystore, manifests) and approval to use it.
- Final approval before any tag, GitHub release or distribution of installers.
