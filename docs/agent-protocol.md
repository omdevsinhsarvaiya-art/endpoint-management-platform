# Agent protocol

Status: **v1, Phase 0 skeleton.** This document defines the frame that Phase 1
fills in. Constants live in `shared/Contracts/AgentProtocol.cs` and are
compiled into both the server and the agent, so names cannot drift.

## Principles

1. **Agent-initiated only.** The agent polls/connects outbound over HTTPS.
   Endpoints never open inbound ports and the server never connects to an
   agent.
2. **Versioned.** Every request carries `X-Agent-Protocol-Version` (currently
   `1`). The server rejects versions it does not understand rather than
   guessing, so mixed-version fleets fail loud, not weird.
3. **Per-device identity.** Enrollment (Phase 1) issues each device its own
   credential. There is no fleet-wide shared secret, so one compromised
   endpoint never impersonates another.
4. **Idempotent where possible.** Heartbeats and inventory uploads can be
   retried safely; the server deduplicates on device identity + timestamp.

## Transport

- HTTPS. In production the server certificate must chain to a trusted root;
  the agent refuses `AllowUntrustedServerCertificate` outside Debug builds.
- Route prefix: `/agent/v1` on the Agent API host only.

## Headers

| Header | Direction | Meaning |
|---|---|---|
| `X-Agent-Protocol-Version` | request | Protocol version (`1`) |
| `X-Agent-Device-Id` | request | Enrolled device id (absent during enrollment) |
| `X-Agent-Version` | request | Agent build version, for fleet upgrade visibility |
| `X-Correlation-Id` | both | Request tracing; server-generated if absent/invalid |

Authentication headers are specified in Phase 1 together with the credential
scheme (see ADR placeholder in `docs/adr/`).

## Endpoints (defined; implemented in Phase 1+)

### `POST /agent/v1/enroll` *(Phase 1)*
Exchange a scoped, expiring, limited-use enrollment token for a device record
and a long-term device credential. The token is stored server-side only as a
hash. Re-enrollment of a machine with a known machine identifier updates the
existing device rather than creating a duplicate.

### `POST /agent/v1/heartbeat` *(Phase 1)*
Authenticated. Body: hostname, agent version, timestamp, basic status. Server
updates `last_seen`; online/offline is derived server-side from heartbeat
staleness, so a dead agent cannot lie about being alive.

### `POST /agent/v1/inventory` *(Phase 2)*
Authenticated. Full hardware/network/software inventory snapshot.

## Error handling

Errors are RFC 7807 problem-details with a `correlationId` extension. The
agent treats `401/403` as "credential invalid — do not retry with the same
credential", `409` as "identity conflict — surface to operator", and `5xx`
with exponential backoff + jitter.
