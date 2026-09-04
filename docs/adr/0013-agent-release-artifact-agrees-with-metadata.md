# ADR-0013: An agent release's artifact must agree with its metadata

Status: accepted

## Context

Agent release 1.5.1 was registered and published carrying the exact bytes of
release 1.5.0 — the same SHA-256. The row said 1.5.1; the package inside said
1.5.0. It was revoked before any device installed it, so the fleet was not
looped. But nothing had *prevented* it: the version came from the upload form,
the bytes were hashed at upload and re-hashed at publish, and at no point were
the two compared. The gate proved the bytes were the bytes; it never asked what
the bytes were.

Two facts about a release therefore lived independently: what the administrator
typed, and what Windows Installer would report once the package was on a
machine. Every downstream decision — "is this device outdated?", "is this a
downgrade?" — reasoned from the typed one.

## Decision

**Two mode-independent checks, applied when a release is registered and again
when it is published, in every trust mode.**

1. **The package's own ProductVersion must equal the declared version.** The
   value is read out of the MSI's Property table by the server itself, on
   Linux, with no Windows Installer present (`MsiDatabase`: the string pool and
   one table, nothing more). A package whose version cannot be read — no
   database inside the compound file, no Property table, no ProductVersion row,
   streams that do not decode — is refused, not assumed. The declared version is
   **never rewritten** to match: the requirement is that the two agree, not that
   one wins.

2. **One package is one release.** Bytes already recorded as any release's
   artifact — Draft, Published *or Revoked* — cannot be registered or published
   under another version. Revoked counts because history is what this protects:
   the bytes once published as 1.5.0 can never afterwards be anything else.

Both refusals are audited as failures under the existing actions
(`agent_release.created`, `agent_release.published`, `agent_release.artifact_replaced`)
with a category in the failure reason — `ProductVersionMismatch`,
`ProductVersionUnavailable`, `DuplicateArtifact`, alongside the existing
`NotAnMsi`, `HashMismatch`, `ArtifactMissing` — so the trail is one query.

**What registration still leaves to publish:** only the trust mode's own
requirement. Under Public an unsigned draft may still be registered and have its
artifact replaced with the signed build; that path is unchanged. Under Internal
there is nothing left to leave.

**What does not change:** the Internal trust model, the server-computed SHA-256,
authorization, the one-way lifecycle, and every check ADR-0012 lists. The
Authenticode verifier is still never reached under Internal; the new checks run
before the mode is consulted, and a test asserts the verifier's call count stays
at zero whichever way they go.

## Consequences

- A compound file that is an MSI in shape only is no longer an MSI as far as
  releases are concerned. The test artifact writer now emits a real database, so
  every test artifact carries the version it is registered as.
- **History is untouched.** 1.5.0 stays Published and is served exactly as
  before; 1.5.1 stays Revoked, undownloadable, unpublishable, and listed. Only
  the Draft → Published transition is gated, and Revoked is terminal before the
  gate is reached. Tests reproduce the pair and assert both.
- **No schema change.** `version` and `sha256` already hold both facts. A
  separate ProductVersion column would only create a third thing that could
  disagree with the other two.
- Reading the real package exposed two things the generated test artifacts had
  agreed with the reader in getting wrong, and both are now pinned by tests
  against WiX's actual output: the string pool streams carry the database
  marker like the tables do, and a package's final sector may be short — the
  built agent MSI ends 3,085 bytes into its last 4 KB sector. `CompoundFile`
  used to refuse any sector it could not read in full, which would also have
  reported a signature stream ending there as "unsigned" under Public; it now
  reads a short final sector and still refuses a stream that claims more bytes
  than the file holds.
- A refused upload leaves its bytes in the content-addressed store, as a refused
  hash already did. Orphaned content is inert: no row points at it, and the
  address is the hash, so a later legitimate upload of the same build simply
  finds it there.
- The console shows the server's refusal as written — "Declared release: 1.7.1 ·
  MSI ProductVersion: 1.7.0" — and re-reads the release list after a refusal, so
  a row is only ever shown as Published when the server says it is.

## Alternatives rejected

- **Rewrite the declared version from the package.** Silently changes what the
  administrator asked for. Applied to the 1.5.1 upload it would have produced a
  second 1.5.0 row — refused by the unique index, but for a reason that names
  the wrong problem — or, without the index, two identical releases.
- **Store ProductVersion in a new column.** A third fact where two already have
  to agree; a migration for a value that must never differ from one it would
  sit beside.
- **Check only at publish.** A draft that lies is a draft someone will publish.
  The registration check gives the operator the refusal at the moment they can
  act on it, with the file still in hand.
- **Read the version with Windows Installer.** The API runs on Linux. And even
  on Windows, handing an untrusted upload to `msi.dll` to ask its version is a
  larger attack surface than reading two streams.

## References

- `server/Infrastructure/Agents/MsiDatabase.cs` — the reader
- `server/Infrastructure/Agents/ReleasePublishVerifier.cs` — where the check sits
- `server/Infrastructure/Agents/AgentReleaseService.cs` — registration, publish, replacement, and the audited refusals
- [ADR-0012](0012-agent-release-trust-model.md) — the trust model this leaves intact
- [threat-model.md](../threat-model.md) — T10
