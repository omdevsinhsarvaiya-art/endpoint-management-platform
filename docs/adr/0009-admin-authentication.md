# ADR-0009: Admin authentication, sessions and permission enforcement (Phase 3)

Status: accepted (Phase 3)

## Decisions

### Passwords
PBKDF2-HMAC-SHA256, 600k iterations, per-hash 128-bit salt, encoded with its
parameters (`pbkdf2-sha256$iter$salt$hash`) so policy can be raised later;
verification tolerates and reports weaker legacy parameters (rehash-on-login).
Framework primitive (`Rfc2898DeriveBytes.Pbkdf2`), no hand-rolled crypto.
Distinct from machine-credential hashing (`SecretGenerator`) by design — human
passwords are low-entropy and need stretching; 256-bit CSPRNG secrets do not.

### Sessions — opaque server-side, not JWT
256-bit random token; server stores SHA-256 only. Revocation is a row update.
The session pins the user's security-stamp at sign-in; any credential/role
change or account disable rotates the stamp and kills every outstanding
session on its next request (tested). Absolute 12h lifetime.

### Transport — HttpOnly cookie first, Bearer for tooling
The dashboard is same-origin with the API (dev proxy now, reverse proxy in
deployment), so the token rides an HttpOnly, Secure, SameSite=Strict,
`__Host-` cookie — script cannot read it, XSS cannot exfiltrate it. CSRF
defence in depth: SameSite=Strict plus a required `X-Requested-With` header on
cookie-authenticated mutations (middleware-enforced, tested live).
`Authorization: Bearer` with the same opaque token serves CLIs and tests; the
login response includes the token for that purpose and the dashboard ignores it.

### Authorization — permission claims, dynamic policies
The authentication handler resolves the user's effective permissions
(user→roles→role_permissions) fresh per request into claims; endpoints declare
`.RequirePermission(Permissions.X)`; a policy provider builds `permission:*`
policies on demand and **throws on unknown keys**, so a typo'd permission fails
at first use instead of silently denying everyone. No role names in code.

### Denial auditing
An `IAuthorizationMiddlewareResultHandler` writes a `Denied` audit entry
(actor, required permission, path) whenever an authenticated user hits a
permission wall. Anonymous 401s are deliberately not audited — scanner noise
would let an attacker fill the audit table; request logs cover them.

### Sign-in hardening
Uniform 401 for unknown account / wrong password / disabled / locked, with a
dummy PBKDF2 verification equalising timing for unknown accounts; account
lockout (5 fails / 15 min, configurable); per-address fixed-window rate limit
on the login endpoint (configurable, in-memory — Redis-backed is Phase 15);
every attempt audited (success and failure).

### Bootstrap
`dotnet run --project server/Migrations -- bootstrap-admin` with email and
password from environment variables only. Refuses when any Super Administrator
exists. Audited as a system action.

## Consequences
- Every Admin API endpoint now requires a permission; the Phase 1 synthetic
  actor is gone and the threat-model limitation is closed.
- Session validation costs one indexed DB lookup + one permission query per
  request; caching is deliberately deferred until measurement says otherwise.
- In-memory rate limiting resets on restart and is per-instance; acceptable at
  MVP scale, listed for Phase 15.
