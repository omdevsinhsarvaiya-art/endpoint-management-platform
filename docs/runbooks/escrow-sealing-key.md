# Provisioning the escrow sealing keypair

Automatic BitLocker recovery-password escrow seals on the endpoint. Endpoints
encrypt to a **public** key; only the **private** half can read the result back.
Splitting that pair across the two APIs is the control that keeps the
endpoint-facing process unable to read what it stores.

This runbook contains no key material and never will. Everything below is run on
the host; nothing is pasted into source control, a ticket, or a chat window.

## Where each half goes

| Half | Process | Setting | Why |
|---|---|---|---|
| Public (SPKI) | **admin-api and agent-api** | `RecoveryEscrow:SealingPublicKey` | Encrypts only. Agent API pins it at enrollment and rejects envelopes sealed elsewhere. |
| Private (PKCS#8) | **admin-api only** | `RecoveryEscrow:SealingPrivateKey` | Anything holding this can read every automatically escrowed recovery password. |

Two startup guards enforce this and both fail the host rather than warn:

- **Agent API** refuses to start if `RecoveryEscrow:SealingPrivateKey` or the
  master `RecoveryEscrow:Key` appears in its configuration.
- **Admin API** refuses to start if a public key is configured without its
  matching private half, or if the two are not the same pair. Without that check,
  every escrow would *succeed* while nobody could ever open one — discovered on
  the day a disk will not boot.

Leaving both empty is a valid state. Automatic escrow is simply off: no device
becomes eligible, and manual escrow is unaffected.

## Generating the pair

Run on the host, as the user that owns `infra/.env`:

```sh
umask 077
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
  -outform DER -out /tmp/sealing.der

# RECOVERY_SEALING_PRIVATE_KEY
base64 -w0 /tmp/sealing.der

# RECOVERY_SEALING_PUBLIC_KEY
openssl pkey -inform DER -in /tmp/sealing.der -pubout -outform DER | base64 -w0

# The fingerprint agents pin. Useful for verification; not a secret.
openssl pkey -inform DER -in /tmp/sealing.der -pubout -outform DER \
  | openssl dgst -sha256 -hex

shred -u /tmp/sealing.der
```

Write both values into `infra/.env` (mode `600`), then restart the stack. The
private key is **unrecoverable if lost**: back it up wherever the master escrow
key is backed up, because every automatically escrowed password depends on it.

## Verifying without exposing anything

```sh
# Both halves present, and the pair matches: the Admin API starts.
docker compose logs admin-api | grep -i "sealing"

# The Agent API serves the public key and its fingerprint; neither is a secret.
curl -s .../agent/v1/bitlocker/escrow-status -H '...' | jq '.sealingKeyFingerprint'
```

Compare that fingerprint against the `openssl dgst` output above. They must be
identical. If they differ, the two services have been given different keys and
automatic escrow will be refused at ingestion — envelopes are rejected, nothing
is lost, but nothing is collected either.

## Rotation

Rotation is **not** automatic, and the current build does not implement
continuity proof. What happens today:

- New escrows seal under the new key.
- Existing rows stay readable: each records its `key_version` and `seal_scheme`.
- **Agents pinned to the old fingerprint refuse the new key** and stop escrowing
  until they re-enroll. This is the pin working, not a fault.

So a rotation is a fleet re-enrollment. Plan it as one.

## Turning automatic escrow on

Deliberately several deliberate steps, in this order:

1. Apply the `AutomaticBitLockerEscrow` migration.
2. Provision the keypair as above and restart both APIs.
3. Deploy an agent build that contains the escrow runner.
4. **Re-enroll** the devices that should participate. A credential issued before
   the keypair existed carries no pinned fingerprint, and an unpinned device
   never reads a recovery password. There is no trust-on-first-use path, by
   design.

Devices that are not re-enrolled keep working: full BitLocker inventory, manual
escrow, and a console status reading *automatic escrow unavailable —
re-enrollment required*.
