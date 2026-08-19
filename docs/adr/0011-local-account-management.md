# ADR-0011: Windows local account and group management

## Status

Accepted.

## Context

Operators need to manage Windows local accounts on managed endpoints: create and
delete users, enable and disable them, reset passwords, force a password change,
and — most importantly — move an account between Standard User and Administrator.

Two constraints shaped every decision:

1. **ADR-0005** forbids the agent from launching a process or a shell. A
   `net localgroup administrators user /add` one-liner is exactly the pattern that
   ADR exists to prevent.
2. Administrator status must be *real Windows state*. A database column saying
   "this user is an admin" is a claim about the world, not the world; the two
   drift the moment anything changes the machine out of band.

## Decision

### Administrator means BUILTIN\Administrators membership

Promotion adds the account to the local Administrators group (well-known SID
`S-1-5-32-544`) and demotion removes it. The group is addressed by SID and
resolved to its local name, so this works on localized Windows. Nothing anywhere
stores "is an administrator" as an authoritative flag: `IsLocalAdministrator` in
inventory is an *observation* refreshed from the endpoint, and the dashboard
reconciles against reported inventory after every change rather than assuming the
requested state took effect.

### Mutations use netapi32, not a shell

`WindowsLocalAccountControl` calls the account-management APIs directly:

| Operation | API |
|---|---|
| Create user | `NetUserAdd` (USER_INFO_1) |
| Delete user | `NetUserDel` |
| Enable / disable | `NetUserSetInfo` level 1008 (UF_ACCOUNTDISABLE) |
| Reset password | `NetUserSetInfo` level 1003 |
| Force password change | `NetUserSetInfo` level 1017 (password age 0) |
| Set display name | `NetUserSetInfo` level 1011 |
| Add to group | `NetLocalGroupAddMembers` (LOCALGROUP_MEMBERS_INFO_0, by SID) |
| Remove from group | `NetLocalGroupDelMembers` |
| Live account read | `System.DirectoryServices.AccountManagement` |

These take typed parameters and have no command line to inject into: a username
containing `& del *` is a username that fails validation, not an injection. The
`AgentSafetyTests` scan (no `Process.Start`, no PowerShell SDK, no process
reference in Core) passes unchanged.

Net\* functions return their status directly rather than setting the last error,
so results are checked against the returned value, never `GetLastWin32Error`.

### Targets are identified by SID

A local account can be renamed; its SID cannot. Every payload targets a SID, with
the last-known name carried only for logging and result messages, so a task queued
minutes ago cannot land on the wrong account because someone renamed one.

### Nine typed tasks, no generic mechanism

`CreateLocalUser`, `DeleteLocalUser`, `EnableLocalUser`, `DisableLocalUser`,
`ResetLocalUserPassword`, `ForceLocalUserPasswordChange`, `ChangeLocalUserType`,
`AddLocalUserToGroup`, `RemoveLocalUserFromGroup`. Each has a catalog entry
(permission + high-risk + TTL) and one executor. `DeviceTaskCatalog.Require`
fails closed, so a task type without an entry cannot be queued at all.

### Passwords never persist

Task payloads are stored in PostgreSQL and mirrored into the audit trail, so a
password cannot travel in one. Instead:

1. The API hands the plaintext to an **ephemeral secret store** (Redis, dedicated
   key namespace, AES-GCM sealed, 15-minute TTL).
2. The persisted task carries only an unguessable **reference**.
3. The agent redeems it **once** over its authenticated channel; redemption is an
   atomic `GETDEL`, so a replay — or a reference stolen from a task row — yields
   nothing.
4. The reference embeds the device id and redemption verifies it, so one agent
   cannot redeem another's secret.

If the secret store is unreachable the operation is **refused**, never downgraded
to putting the password in the payload.

### Safety rules, enforced twice

Two protections stop an operator locking the organization out of a machine:

- **Protected account**: the built-in Administrator (RID 500) cannot be deleted or
  disabled.
- **Last administrator**: deleting, disabling, or demoting the last *enabled*
  member of the Administrators group is refused. A disabled administrator does not
  count as the safety net, because it cannot recover the machine.

The API pre-checks these against last-reported inventory (a fast, friendly
refusal) and the **agent re-checks them against live Windows state** immediately
before acting, because inventory can be stale. Removing someone from the
Administrators group gets the same guard as an explicit demotion — it is the same
act by another name.

### Every created account joins BUILTIN\Users

`NetUserAdd` creates the SAM account and **joins no groups at all.**
`usri1_priv = USER_PRIV_USER` reads like it sets the account's privilege level,
but it grants no membership — so accounts created by this platform initially had
*zero* local group memberships.

The failure was invisible in the worst way: such an account **still signs in**,
because `BUILTIN\Users` contains `NT AUTHORITY\Authenticated Users`, and its
rights are granted at logon through that path. Nothing was broken from the user's
seat. Only `net user <name>` — reporting `Local Group Memberships *None*` —
showed it.

Every account the platform creates is therefore explicitly added to
`BUILTIN\Users`, addressed by **well-known SID** (`S-1-5-32-545`) for the same
reason Administrators is: the group is named differently on localized Windows and
a name lookup would fail there. The membership is then **verified from Windows**
after creation, and a failure to establish it rolls the account back like any
other post-create failure.

This applies to **administrators too**, not only standard users. Demotion removes
the account from Administrators; an administrator that never joined Users would
land in exactly the groupless state this fixes. The baseline has to hold before
the demotion rather than be repaired after it — so `ChangeLocalUserType` also
establishes Users membership *before* dropping Administrators, leaving no window
in which the account belongs to neither.

Post-create verification asserts both directions: an administrator must really be
in Administrators, and a standard user must really *not* be. A standard account
that somehow acquired administrator rights is rolled back rather than reported as
a success.

### Profiles are portable; additional groups are optional and device-filtered

A configuration profile is a baseline that must apply on **every Windows SKU**, so
no profile names a group that only some editions have. The "IT Administrator"
baseline originally defaulted to `Remote Desktop Users`; on a Home edition, which
has neither that group nor `Backup Operators`, creating an IT administrator failed
outright and the account was rolled back — over an optional extra that was never
the point of the request. Administrator rights come from the account type; the
baseline now adds no groups.

The `PermittedAdditionalGroups` allow-list is a **policy ceiling, not a claim that
the groups exist.** Two consequences follow:

- **What an operator is offered is the allow-list ∩ the device's reported groups**
  (`UserConfigurationProfiles.PermittedGroupsPresentOn`, computed in the domain so
  policy and availability cannot drift apart, and served per-device from
  `GET /local-user-profiles`). A device that has never reported groups is offered
  the full list — no inventory is missing knowledge, not evidence of a machine with
  no groups.
- **The agent skips an additional group the machine does not have** rather than
  failing the create, and names it in the task result. The account is what was
  asked for; an absent optional group is not worth destroying it. The skip is
  reported, never silent — a success that quietly delivered less than requested is
  the same class of defect as a failure that quietly left state behind.

`Administrators` is exempt from all of this. It is never in the allow-list and
never optional: it is requested as an account type, gated by `user.change_type`,
verified against live Windows state after creation, and a failure to achieve it
rolls the whole account back. Routing it through "additional groups" would be a way
around the permission check and the last-administrator safeguards.

### Authorization is permission AND scope

Permission says what an operator may do; **device scope** says where. A new
administrator has no scope and reaches nothing until scope is granted explicitly.
Administrators predating this model were migrated to organization-wide scope, so
"no scope rows" never has to mean "unlimited" — the inverse would make every
future account silently omnipotent.

## Consequences

- **The agent must run elevated.** Local account mutation requires administrator
  privilege; as a Windows service the agent runs as LocalSystem, which satisfies
  this. An agent running as a standard user reports a genuine access-denied
  failure rather than silently doing nothing.
- Group membership is read from denormalized inventory JSON, so the "which groups
  is this user in" view is as fresh as the last inventory upload, not live. The
  dashboard therefore shows `Groups: Users` because Windows *reported* that
  membership, never because a create task was marked successful — which is what
  made the missing-Users defect visible at all. The same
  staleness applies to which groups are offered at creation time, which is why the
  agent tolerates an absent one instead of trusting the list it was given.
- A `netapi32` dependency is added to `EndpointAgent.Windows`. It is a core
  Windows DLL, so this adds no third-party surface.
- Creating a user with a password requires Redis. This is an existing dependency,
  and the failure mode is a refused operation, not a leaked secret.
