# PaRuX branch and PR reconciliation — 2026-11-05

Scope: GitHub `Ruba94-cyber/PaRuX-AI-Gaming-OS` at the current 2026-11-05 `main`. **Source/branch audit only**, not Windows runtime evidence.

## Canonical integration decision

- Exactly one active implementation branch: `main`. No blanket merges, no branch deletion, no new GitHub Actions, no release or tag.
- Six historical PRs (#1–#6) were already merged into `main`, with no open PRs during the audit. Their branch heads diverge after squash merges; divergence alone does not mean the features are absent.
- Recursive Git tree inventory covered all ten non-main branches. Every old application/test source path already exists on `main`; the sole old-branch-only blob is `.ai/telemetry/claude-account-switch.log`. This contains operational logs and must **not** be published or copied.
- Four historical GOS/foundation branch refs are substantially older; six `remote/l*` branch refs are PR-era snapshots. Keep them archived-in-place until an explicitly authorized cleanup after Windows tests.

## Branch-by-branch decision

| Branch | Git relation at audit | Action on main |
|---|---|---|
| `checkpoint/gos-continuation-20261031` | Behind `main` | Older checkpoint; no independent source path to merge |
| `feature/gos-owned-process-safety-20261031` | Behind `main` | Older safety checkpoint; preserve current identity checks |
| `feature/parux-ai-gaming-os-foundation` | Diverged (five legacy-only commits at audit) | Selectively recovered CLI help, Computer Use fail-closed checks, native runner diagnostics; did **not** overwrite newer UI or import internal telemetry |
| `feature/parux-ai-gaming-os-public` | Behind `main` | Older publishing baseline; no independent source path to merge |
| `remote/l2-coding-review-policy-20261105` | Diverged after PR #1 merge | L2 parser/preview files and tests already on `main`; no repeat merge |
| `remote/l3-chat-compare-media-presets-20261105` | Diverged after PR #2 merge | L3 model comparison contracts/endpoints/tests already on `main` |
| `remote/l4-l5-external-library-prices-20261105` | Diverged after PR #5 merge | L4/L5 preview, price and wishlist components already on `main` |
| `remote/l6-l7-suspend-controller-planners-20261105` | Diverged after PR #4 merge | L6/L7 planners and tests already on `main` |
| `remote/l8-local-game-chat-resource-policy-20261105` | Diverged after PR #3 merge | L8 policy already on `main`, expanded with a scoped owner API and IT/EN Chat draft handoff |
| `remote/l-roadmap-handoff-20261105` | Diverged after PR #6 merge | Historical source reconciled in the canonical execution plan and handoff |

**Caveat:** File presence and squash-merge ancestry establish coverage of the original PR scopes, but not byte-for-byte behavioral equivalence or successful runtime tests. Old branch snapshots can contain alternate behaviors (for example first-model onboarding wording), which should not automatically replace the current UX. Preserve those historical refs for review, not as active release branches.

## Recovered missing behavior from legacy foundation

1. Agent CLI `agent --help`, `agent -h`, `agent help`, `agent run --help`: correct no-Core, no-task help with synthetic child-process tests.
2. Computer Use GUI proof: reject a malformed window in any readback, refuse null observations for cross-window proof, and re-attest selected app lease/allowlist before final single-window success.
3. Windows validation runner: capture .NET/Node native stdout and stderr separately while preserving actual exit code; added isolated PowerShell/native failure sentinels.

These were selectively ported onto current `main`; older branches were not merged verbatim. Onboarding and other historical UI code was not force-applied because it has subsequently evolved. The presence of a differing hash cannot establish that the older version is correct.

## Remaining mandatory validation

On a Windows developer PC, run the PowerShell runner's `-SelfTestNativeProcessCapture`, the GOS build/xUnit/FlaUI gates, full Server xUnit, separate sequential UI smoke/regression, and physical GPU/controller/game/mod/torrent checks. Treat every source change here as **SOURCE WIP** until those checks succeed. `PARUX_EXECUTION_PLAN.md` is the canonical remaining-work tracker.
