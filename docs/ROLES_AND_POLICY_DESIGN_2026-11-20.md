# Roles and per-principal policy: design note (2026-11-20)

Status: **design only, nothing built.** Corporate roles, per-user policy and an audit trail are listed MISSING in the execution plan. The
audit trail now exists (hash-chained local log); this note covers the rest. Written from the code as it is, not from competitor docs.

## What exists today

Every request is classified in `Program.cs` into one **principal**, stored in `HttpContext.Items["PaRuXOwner"]`:

| Principal | Value | Reached by |
|---|---|---|
| Desktop owner | `desktop` | loopback with the app's own token |
| Browser session | `web:<sessionId>` | passphrase portal over the LAN |
| Paired device | `device:<deviceId>` | mobile pairing |
| API key | `apikey:<keyId>` | OpenAI-compatible clients |

Authorisation is two lists, both reviewed by tests: `RequireDesktopOwner()` on 435 call sites, and
`web-session-route-decisions.txt` (every route has ALLOW or DENY for a browser session); paired devices have
`PairedDeviceEndpointPolicy`. So the model is already "owner can do everything, everyone else gets a fixed allow-list". What is missing is
a way for the owner to **choose** what each non-desktop principal may do.

## Goal

The owner gives each browser session, paired device and API key a **role**, and PaRuX enforces it in one place. No new accounts, no cloud,
no passwords stored beyond what pairing already has. "Multi-user" here means several principals with different rights on one PC or one
LAN, not separate user profiles with separate data.

## Roles

| Role | May | May not |
|---|---|---|
| `owner` | everything (the desktop principal only; cannot be given to others) | n/a |
| `operator` | chat, documents, Create, Model Hub reads, start and cancel jobs | change settings, install anything, plugins, webhooks, API keys, pairing, Internet policy, file system paths |
| `viewer` | read chats it created, read job status, read library | anything that changes state or costs compute |
| `api-chat` | the OpenAI-compatible chat, completions, embeddings, models routes only | everything else |

Defaults preserve today's behaviour: an existing paired device becomes `operator`, an existing API key becomes `api-chat`, a browser
session becomes `operator`. Nothing gets more rights than it has now.

## Enforcement

One endpoint filter, `RoleGate`, runs after principal classification and before the handler. For each route it needs one of three
labels: `owner-only`, `operator`, `read`, or `api`. Labels come from a single table extending `web-session-route-decisions.txt`
(a new column), and a unit test fails when a registered route has no label, the same way the existing test does. The existing
`RequireDesktopOwner()` calls become the `owner-only` label, so nothing is weakened by the migration.

## Data

`principal-roles.json` in the state directory: `{ principalId, role, grantedUtc }`, atomic write, size bounded, damaged file means
"defaults" (never "more rights"). Granting or changing a role writes an audit entry (`role.grant`, `role.revoke`).

## Tests that must exist before this ships

1. Every route has a label; unlabeled route fails the build.
2. Matrix test: for each role and each label, the expected 200 or 403, on the real pipeline.
3. Downgrade test: changing a device from `operator` to `viewer` takes effect on its next request and cancels its in-flight jobs.
4. Migration test: an installation with existing devices and keys keeps exactly its present rights.
5. Audit test: every grant, change and revoke is in the hash-chained log.

## Risks

* 435 `RequireDesktopOwner()` call sites and the route-decision file must be re-reviewed once; a mislabeled route is a privilege bug.
  Mitigation: the matrix test plus a review of every route that is not `owner-only`.
* Streaming and long jobs outlive a role change; the gate must also check at job start and on cancel.
* The Store edition and Corporate Lite share the code; roles must default to off in the Store edition (no LAN features there).

## Not in this design

Separate data per user, SSO, an admin console, remote policy distribution. They need a different threat model.
