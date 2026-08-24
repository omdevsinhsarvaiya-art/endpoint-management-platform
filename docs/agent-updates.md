# Agent releases and self-update

How new agent builds are distributed, how an enrolled machine updates itself,
and exactly what is — and is not — guaranteed.

## The release model

An **agent release** is one distributable MSI build: version (three numeric
parts), platform/architecture (`windows/x64`), filename, SHA-256, an optional
Authenticode signer subject, release notes, and a one-way lifecycle:

```
Draft ──publish──▶ Published ──revoke──▶ Revoked   (terminal)
```

A revoked release can never be re-published; upload a fresh build instead, so
every audit entry refers to an immutable artifact. Uploading is
`Software.Deploy`; viewing and downloading are `Software.View`. The MSI bytes
live in the same content-addressed store as software packages, keyed by hash —
the store recomputes the SHA-256 while writing and refuses a mismatch, so a
release row can never describe bytes it does not have.

Releases are deliberately **not** software packages. Packages are per-tenant
and installed in-process by the agent; a release is platform infrastructure
with version ordering, and its installer must survive the agent's own death
(below).

## The update flow

```
administrator publishes a release
        ↓
dashboard shows "Update available" per outdated device
        ↓
administrator queues UpdateAgent (typed task; Software.Deploy; audited)
        ↓
agent claims the task and re-fetches release metadata over its own
authenticated channel — the payload is a claim, never an instruction
        ↓ refuse on any mismatch with the server's offer
agent enforces: strictly newer version, x64, then downloads
        ↓
SHA-256 over the actual bytes            → mismatch: DO NOT INSTALL
        ↓
Authenticode via WinVerifyTrust + signer-subject pin (when the release
declares a signer)                        → failure: DO NOT INSTALL
        ↓
state snapshot (credential + enrollment files → update-backup/)
        ↓
one-shot Task Scheduler entry: msiexec /i <verified file> /qn
        ↓ task result posted: "update started" — never "succeeded"
installer stops the service, upgrades, starts the new one
        ↓
new agent restores state from the snapshot if the installer removed it
        ↓
same DeviceId, same credential, new version on the next heartbeat
```

Task Scheduler is what makes this survivable: the upgrade stops the agent
service, so nothing running inside the agent can carry the install to
completion. The scheduled entry is executed by Windows as SYSTEM, decoupled
from the agent's lifetime. It is not a command channel — the executable is the
fixed system `msiexec.exe` and the only variable content is the path of a file
the agent itself just verified.

## Version policy

- Comparison is numeric, three parts (`1.0.10 > 1.0.9`; never lexicographic).
- **No downgrades and no same-version reinstalls** — enforced independently by
  the server (when queueing) and by the agent (when executing). Recovering from
  a bad release means publishing a *newer* fixed build, which also keeps the
  audit history linear.
- Unparseable versions are never "newer": every comparison fails closed.
- Updates are administrator-initiated per device. There is no automatic
  fleet-wide push; the release being published makes it *available*, not
  *mandatory*.

## Failure behaviour

| Failure | Outcome |
|---|---|
| download interrupted | partial file deleted; current install untouched |
| hash mismatch | refused before signature check; file discarded |
| signature/pin failure | refused; file discarded |
| downgrade / same version | refused before any download |
| wrong architecture | refused before any download |
| task vs server mismatch | refused before any download |
| install fails mid-upgrade | Windows Installer rolls the transaction back; the old version is restored from the cached MSI |
| device offline | task waits Queued; expires after its TTL if never claimed |
| agent dies holding the task | task expires via the sweeper; a late result is rejected |

There is no bespoke rollback beyond MSI's own transactional rollback, and none
is claimed: a failed *execute phase* restores the previous version; a machine
that wants the previous version after a *successful* install gets it by
publishing that build as a newer version number.

## Identity preservation

The state directory (`C:\ProgramData\EndpointPlatformAgent`) holds the DPAPI
credential and enrollment identity. From 1.1.0 the installer never removes it —
not on upgrade and not on uninstall. Removing a machine from management is a
server-side retire (which revokes the credential and makes the residue
useless); cleaning the directory afterwards is a documented manual step.

Belt and braces: before scheduling an install the agent snapshots its state
files, and the freshly started service restores any that went missing, then
consumes the snapshot. This exists for one concrete reason:

> **Known limitation — the 1.0.0 → 1.1.0 hop.** The 1.0.0 installer removed
> the state directory during upgrades (its uninstall ran `RemoveFolderEx`, and
> a major upgrade runs the old uninstall). A machine upgraded manually from
> 1.0.0 therefore re-enrolls — same device row via its machine identifier, but
> requiring a fresh approval. 1.0.0 agents also predate the `UpdateAgent`
> executor, so a task-driven update queued to one fails honestly as
> "Unsupported task type". The first hop is manual; every hop after 1.1.0 is
> remote and identity-preserving.

## Verified on real Windows

Both hops were run end to end against an enrolled physical machine on the
production deployment, not a test double.

**1.0.0 → 1.1.0 (manual, the documented one-time hop).** MSI downloaded from
the dashboard's Agent page and installed by hand. As predicted, the 1.0.0
uninstall had already destroyed the DPAPI credential, so the agent raised a
fresh enrollment request, which an administrator approved in the dashboard.
Result: back to Active/Online reporting **1.1.0**, resolved by machine
identifier to the **same DeviceId** — one device row, no duplicate — with
inventory and heartbeat resuming on cadence.

**1.1.0 → 1.1.1 (remote self-update, the mechanism this document describes).**
Release published, `UpdateAgent` queued from the dashboard. The agent
re-fetched the release metadata over its own authenticated channel, verified
version, architecture and SHA-256, scheduled the install, and reported *"Update
to 1.1.1 verified and started"* — never "succeeded", because the process
saying it was about to be stopped. The device returned online **40 seconds**
later reporting **1.1.1**, with:

- the same DeviceId and machine identifier,
- **the same agent credential** (identical key id before and after),
- no duplicate row and **zero pending enrollments** — no re-enrollment at all,
- inventory advancing afterwards (274 services, 60 processes, 6 users, 37 NICs).

Re-queueing the same version afterwards is refused with 409, so a duplicate
update task cannot be created against an already-current device.

The Milestone 9 actions that the Session 0 fix made possible were exercised on
the updated agent: `LockDevice` → *"Workstation locked."* and `SignOutUser` →
*"Interactive user signed out."*

## Building a release

`.github/workflows/build-agent-msi.yml` builds the MSI on a pinned
`windows-2022` runner using this repository's `build-msi.ps1` — the same script
used locally, not a second build definition — with WiX pinned to 5.0.2 and the
SDK taken from `global.json`. It runs on manual dispatch with a version and
server URL, reads the ProductVersion back out of the built package to confirm
it matches what was asked for, publishes the MSI plus a `SHA256SUMS.txt` as
artifacts, and prints the hash in the run summary.

It deliberately **does not publish or deploy**. Promoting a build stays a
human act: upload it on the dashboard's Agent page with that SHA-256, which the
server recomputes while storing the bytes.

## Signing

The platform verifies more than it currently signs:

- **Verification is fully built**: WinVerifyTrust plus a signer-subject pin,
  enforced by the agent whenever a release declares a signer.
- **Signing is not**: there is no code-signing certificate in this
  environment, so current builds are published with a null signer — which the
  dashboard displays as an explicit **Unsigned build** badge, and the agent
  logs loudly before installing on hash verification alone.
- **The CI signing step already exists and is wired**, in
  `build-agent-msi.yml`. It activates only when both
  `AGENT_SIGNING_CERT_PFX_BASE64` and `AGENT_SIGNING_CERT_PASSWORD` repository
  secrets are set: the .pfx is written to the runner's temp directory, used by
  `signtool` with an RFC 3161 timestamp, verified, and deleted in a `finally`
  block. Nothing is echoed and no certificate or key is ever committed.
- With no certificate configured the build still succeeds and the artifact is
  named `…-UNSIGNED`, with the run summary saying so — a silent skip that
  produced an artifact indistinguishable from a signed one would be the
  dangerous outcome.
- To go live: obtain an Authenticode certificate, add those two secrets, and
  set the signer subject when publishing the release. From that point the
  agent's existing signer pin enforces publisher identity on every install. No
  self-signed certificate is used as a stand-in for real trust.
