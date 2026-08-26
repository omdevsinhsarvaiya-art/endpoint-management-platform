# USB and peripheral control

How removable storage is restricted on managed endpoints, how temporary access
is granted, and — set out plainly — what this control does and does not
actually prevent.

## The rule

**A USB storage device with no live grant is restricted.** Not "restricted once
the server says so": restricted by default, including on a machine that has
never enrolled, cannot reach the network, or has just booted with a stick
already in the port. Access is the exception, and it requires a positive,
unexpired, administrator-issued grant naming that exact device.

There are exactly three states, and only two of them can be granted:

| State | What the user gets | How it is reached |
| --- | --- | --- |
| **Restricted** | Nothing. The device instance is disabled, so no drive letter appears. | The default. Also where revoke and expiry land. |
| **Read-only** | Files can be opened and copied *off* the device. Windows refuses writes, creates, renames and deletes. | An explicit grant. |
| **Enabled** | Ordinary Windows read/write access. | An explicit grant, named as such. |

**Restricted is not grantable.** It is the absence of a grant, reached by
revoking rather than by asking for it, and every layer rejects an attempt to
grant it — the domain throws, the API returns 400, the agent drops the entry.
Attaching an expiry to a state that has none would mean a device silently
becoming accessible when the "grant" lapsed.

Read-only is the default everywhere a level is not stated: an omitted API field,
a grant cached by an older agent, an unparseable policy value. Read/write has to
be named explicitly, in exactly that spelling, to be reached at all — the ordinal
`2` and the string `"2"` are both refused, so a payload carrying a bare number
cannot obtain write access without naming it.

Access, when granted, is always:

- **time-boxed** — an absolute deadline between 5 minutes and 24 hours, chosen
  at grant time and never extended;
- **per device** — keyed to one Windows device instance ID on one endpoint;
- **justified and audited** — a reason is required, and the grant, its
  revocation and its expiry are each an audit record. A read/write grant is
  audited as `usb.access.enable` rather than `usb.access.grant`, so the widest
  decisions can be reported on separately from the narrow ones.

Non-storage peripherals — keyboards, mice, hubs, cameras, network adapters —
are inventoried and never restricted. Disabling an input device would lock the
user out of their own machine.

## How it is enforced on Windows

Per-device, documented public API, no shell command and no kernel driver
(ADR-0005):

| State | Mechanism | Effect |
|---|---|---|
| Restricted | SetupAPI `DIF_PROPERTYCHANGE` / `DICS_DISABLE` on the device instance | The device does not start. No volume, no drive letter, nothing to open. |
| Read-only | Device enabled, then `IOCTL_DISK_SET_DISK_ATTRIBUTES` with `DISK_ATTRIBUTE_READ_ONLY` on each disk beneath it | Windows itself refuses writes, creates, renames and deletes. |
| Enabled | Device enabled, then the same IOCTL with the read-only bit *cleared* | Ordinary Windows behaviour. |

The attribute mask is limited to the read-only bit in both directions, so
nothing else Windows tracks on the disk — the OFFLINE bit in particular — is
disturbed.

Restricting is what Device Manager's *Disable* does. The read-only attribute is
set with `Persist = false`, so it governs this endpoint only and does not follow
the stick to other machines — altering someone's hardware is not the platform's
business.

The class-wide alternatives (`StorageDevicePolicies\WriteProtect`, the
removable-storage Group Policy settings) were considered and rejected: both are
all-or-nothing for every removable device on the machine, so neither can express
"this one approved stick, read-only, for the next two hours", which is the
entire requirement.

After setting the attribute the agent **reads it back** and treats the operation
as failed unless the disk reports the state that was asked for.

The two grant paths fail in opposite directions, deliberately. If read-only
cannot be applied, the device is **restricted** instead — never left enabled and
writable, because the alternative is a writable disk the console believes is
read-only. If read/write cannot be fully applied, the device is **left as it
is** and the failure reported: the device ends up narrower than the grant
allows, which is the safe direction, and re-restricting would revoke a decision
an administrator had just made.

## How a grant travels

```
administrator grants access (usb.manage, level, justification, duration)
        ↓ audited; device marked ReadOnly or Enabled with an absolute deadline
ApplyUsbPolicy task queued, carrying the endpoint's COMPLETE grant set
        ↓
agent replaces its cached policy and reconciles every attached storage device
        ↓
enforcement result reported back on the next USB report
```

Two things make this robust:

**The policy is whole state, not a delta.** Every task carries every live grant.
A device absent from the list is restricted, so revocation is the *absence* of
an entry rather than a second message that could be lost. Re-sending is
therefore always safe and repairs drift.

**There are two delivery channels, computed from one source.** The pushed task
is for immediacy; the response to the agent's own USB report is for
convergence. An agent that missed a task — offline when the grant was issued,
or the task expired — gets the right answer the moment the user next plugs
something in. Both channels are built by the same function, so they cannot
disagree, and both fail to the same safe default.

The agent reports on device arrival and removal (WMI PnP notifications) rather
than only on the inventory cycle, so a stick appearing in the console takes
seconds rather than up to a quarter of an hour. Notification is a latency
optimisation, not the mechanism: a periodic reconcile runs regardless, so a
watcher that fails to start delays enforcement rather than losing it.

## Ordering, and the gap it closes

Inside each cycle the agent **enforces before it reports**. A newly attached
device is restricted from the locally cached policy before the server is
contacted, so access never waits on a network round trip. A device the
administrator has already approved therefore goes restricted-then-read-only
within a cycle: the user sees a drive that takes a moment to appear, never one
they could have written to.

## Expiry

A grant expires against the **endpoint's own clock**. The deadline travels with
the grant and is cached on disk, so access ends on schedule on a laptop that has
not reached the server in days.

Three independent things stop a lapsed grant, and any one of them suffices:

1. the agent restricts the device when the deadline passes;
2. the server computes published policy from the clock, so a lapsed grant is
   absent from every policy it hands out — regardless of stored status;
3. a background sweep marks the request `Expired` and returns the console view
   to Restricted.

The sweep is bookkeeping. If it never ran again, no endpoint would keep access
past its deadline; only the console would show a stale row.

## Failure behaviour

Every path lands on Restricted.

| Situation | Outcome |
|---|---|
| never enrolled / offline / server unreachable | restricted (local default) |
| grant cache missing, damaged, or sealed on another machine | restricted; the cache is treated as empty |
| `ApplyUsbPolicy` never delivered, or expired | restricted; converges on the next USB report |
| malformed policy payload | rejected; the previous policy stays in force |
| one malformed or already-expired grant entry | that entry dropped, the rest honoured |
| policy names an access level this agent does not implement | that grant dropped |
| policy names an access level as a number rather than a name | that grant dropped — a bare ordinal can never reach read/write |
| grant cached by an older agent, with no level recorded | read as read-only, never as read/write |
| stale policy arrives after a newer one | ignored (issued-at wins), so a late task cannot reinstate revoked access |
| read-only cannot be applied | device restricted, task reported failed |
| device enumeration fails | nothing evaluated; already-restricted devices stay disabled |
| agent lacks privilege | enforcement fails loudly and is reported unenforced — never silently skipped |

## Decided versus enforced

The console shows two different facts side by side and never collapses them:

- **Policy** — what an administrator decided.
- **Enforcement** — what the endpoint has confirmed it is actually doing.

| Shown | Means |
|---|---|
| Enforced | The endpoint confirmed it is applying the policy. |
| Not confirmed | No report yet — the machine may be offline, or the policy may still be in flight. |
| Drifted | The endpoint reports a different state from the one set. Usually a local administrator changing it by hand. |
| Enforcement failed | The agent could not apply it. **The control is not in place.** |

A console that rendered the desired state as though it were the enforced state
would show a reassuring "Restricted" for a machine that has never been told
anything. The distinction between *Not confirmed* and *Drifted* is kept for the
same reason: only one of them needs investigating.

The agent reports enforcement on **every** USB report, not only after a policy
task, so drift surfaces on the next report rather than never.

## Permissions

| Permission | Grants | Held by |
|---|---|---|
| `usb.view` | See the peripheral inventory and access states | Super Administrator, IT Administrator, Helpdesk, Auditor |
| `usb.manage` | Grant, revoke and re-apply USB storage access | Super Administrator, IT Administrator |

Split deliberately. Seeing which stick is in which laptop is support
information — half of every "my drive isn't showing up" call — while opening a
read path off that laptop is a security decision. Helpdesk gets the first and
not the second. Auditor holds `usb.view` and nothing else here, consistent with
being read-only throughout.

`SystemRoleTests` asserts the whole-catalogue property that *only* those two
roles hold `usb.manage`, so a role added later cannot pick it up quietly.

## Requests come from administrators

An administrator raises and approves the grant in one act, on a user's behalf,
after the user has asked through whatever channel the organisation already uses.
The console is the only place a grant can originate.

There is deliberately no endpoint-initiated path yet. The agent is a
LocalSystem service in Session 0 with no user interface, so accepting requests
from the machine itself means shipping a user-session component — and a local
listener that could approve its own request would be a hole, not a feature. The
request record carries a `source` field (`Administrator` / `Endpoint`) so that
flow stays additive rather than a schema migration.

## Enforcement lasts exactly as long as the agent runs

This is a deliberate boundary, and the most important thing to understand about
how the control behaves in practice: **the product controls USB only while the
agent is running.** Stop the agent and the machine goes back to being an
ordinary Windows PC.

| Agent state | USB storage behaviour |
| --- | --- |
| **Running** | Restricted by default. Read-only only where an administrator has granted it, and only until the grant expires. Enforced locally, with no dependency on the server being reachable. |
| **Stopped** | Not enforced. Devices already attached become usable again; newly inserted devices behave normally. |
| **Restarted** | The persisted policy is reloaded and enforcement is re-established, with no server contact required. A grant that expired during the downtime is not restored. |
| **Uninstalled** | Not enforced, permanently. Nothing this product installed continues to restrict USB. |

The reason this needs stating is that neither mechanism is naturally temporary.
Disabling a devnode writes `CONFIGFLAG_DISABLED` into the device's registry key,
which Windows honours indefinitely — across reboots, across the service being
stopped, and across the product being removed. Left alone, stopping the agent
would freeze the machine in whatever state it was last in, and an administrator
could not lift a restriction because the agent that would receive the
instruction is not running. Uninstalling would be worse: devices would stay
disabled with no remaining mechanism to restore them short of Device Manager, by
hand, per device.

So the agent explicitly stands enforcement down when it stops. It keeps a plain
JSON record — `usb-restricted-devices.json` in the state directory — of every
device instance it has applied state to, and on shutdown it re-enables each one
and clears the read-only attribute.

Two distinctions matter here:

- **Policy is durable; enforcement is not.** The grant set survives a stop, which
  is what lets a restart re-establish the right state offline. Only the
  mechanical enforcement is undone.
- **Release is not the same as revoke.** Revoking a grant returns a device to
  *Restricted*, because the machine is still managed. Release returns it to
  *normal*, because the product is standing down. Collapsing the two would mean
  revoking access handed the user a writable stick.

### The boundary, precisely

Release is user-mode cleanup. It runs when the agent is given the chance to run
it, and not otherwise.

| Ending | Release runs? | Result |
| --- | --- | --- |
| Service stop (`Stop-Service`, SCM stop) | Yes | Devices returned to normal. |
| Service restart, reboot, shutdown | Yes | Released on the way down, re-enforced on the way up. |
| MSI upgrade or uninstall | Yes — the installer stops the service first (`Stop="both"`) | Devices returned to normal. |
| Forced process termination (`taskkill /F`, SCM kill after timeout) | **No** | Devnodes stay disabled. |
| Crash, bugcheck (BSOD), power loss | **No** | Devnodes stay disabled. |

In the failure rows, a previously disabled devnode **remains disabled** until the
agent next starts, at which point the ledger tells it what to release and it
re-applies current policy. Uninstalling the product *while in that state* is the
one case that can leave a device disabled with nothing left to fix it
automatically; the ledger file is what makes that recoverable by hand.

This is not a gap that can be closed from user mode. Windows provides no
mechanism to make a SetupAPI device disable revert automatically when the process
that applied it dies — no lease, no session-scoped handle, no cleanup callback.
Only a kernel-mode filter driver could tie enforcement to a live component, and
this platform deliberately does not ship one (ADR-0005). **No stronger guarantee
than the table above should be claimed for this feature.**

## What this does NOT do

Stated explicitly, because a security control that is oversold is worse than
one that is absent.

- **Read/write access is exactly what it says.** A device under an `Enabled`
  grant behaves like one on an unmanaged machine for the life of the grant: data
  can be copied off the endpoint onto it. The controls that remain are that it is
  time-boxed, keyed to one device instance, attributable to a named
  administrator, and recorded. It is not a data-loss-prevention control.
- **Read-only does not prevent malware.** It stops the endpoint writing *to*
  the device. A malicious file already on the stick can still be copied *from*
  it and run. This is a data-egress and device-hygiene control, not an
  anti-malware one. Nothing here scans, inspects or blocks file content.
- **It does not stop a local administrator.** Someone holding administrator
  rights on the endpoint can stop the agent service or re-enable the device in
  Device Manager. No user-mode agent can prevent that; only a kernel driver
  could, which this platform deliberately does not ship. What the platform does
  guarantee is that such tampering becomes *visible*: the next report shows the
  device as Drifted rather than Enforced. The control is aimed at the ordinary
  user, who cannot do any of that — the grant cache is DPAPI-sealed at
  LocalMachine scope in a directory ACL'd to SYSTEM and Administrators, so a
  standard user can neither read it nor forge one.
- **It does not cover non-USB paths.** Optical drives, SD readers on a
  non-USB bus, network shares, cloud sync clients and personal email are all
  untouched by this feature.
- **There is a sub-second window when access is granted.** Read-only is applied
  after the device is enabled and its disk appears, because the disk does not
  exist while the device is disabled. The agent polls at 100 ms and forces a
  volume re-read, but between the volume mounting and the attribute landing
  there is a brief period where a user actively racing the grant could write.
  Restricted has no such window — the device never starts.
- **It does not track what was copied.** The platform records that access was
  open, to what, for whom, and when. It does not record which files were read;
  doing so would mean an agent that reads and reports file names, which is a
  different feature with a different privacy conversation.

## Verification

Domain invariants, RBAC, the wire format, the agent's fail-closed behaviour and
both HTTP channels are covered by automated suites — including the cases that
matter most: an unreadable grant cache restricting everything, a grant lapsing
with no server contact, a stale policy failing to reinstate revoked access, an
agent's own report being unable to grant it anything, and a numeric enum value
on the wire being dropped rather than honoured.

Enumeration is exercised against real Windows hardware in
`WindowsUsbEnumeratorTests` (read-only SetupAPI queries, safe on any machine):
instance-ID shape and uniqueness, hex vendor/product ids, repeatability, and the
parsing rule that a Windows-synthesised port-path segment is reported as *no
serial* rather than passed off as one — because a grant keyed to a port would
follow the port rather than the approved device.

The agent lifecycle — running, stopped, restarted, uninstalled — is covered by
`UsbAgentLifecycleTests`, which drives one simulated machine across several agent
lifetimes over shared persistent state. It pins the transitions that cannot be
inferred from the single-lifetime tests: stopping releases every device the agent
was enforcing; the policy survives that release; a restart restores enforcement
from local state alone; a grant that expired during downtime is *not* restored;
a device that fails to release is kept for the next attempt rather than
re-restricted; uninstall releases a device even when it is no longer plugged in;
and release touches only devices this agent applied state to, leaving alone any
that an administrator disabled by hand.

Enforcement itself changes real hardware state and is verified on a designated
test endpoint, never on a developer machine or a CI runner. The manual
acceptance script for the lifecycle is:

1. **Running** — attach an unapproved stick; confirm no drive letter appears.
   Grant read-only; confirm the drive appears and a write is refused. Revoke;
   confirm the drive disappears again.
2. **Stopped** — `Stop-Service EndpointPlatformAgent`; confirm the stick becomes
   accessible and writable, and that a *different*, never-seen stick also mounts
   normally.
3. **Restarted** — `Start-Service EndpointPlatformAgent`; confirm the previously
   restricted stick is restricted again without the server being involved
   (verifiable by disconnecting the network first).
4. **Uninstalled** — remove the MSI; confirm both sticks mount and write
   normally, and that `usb-restricted-devices.json` is gone with the state
   directory.

## Acceptance — Milestone 11a, closed 2026-08-27

Signed off against **Agent 1.1.4** on `LAPTOP-LVCHEQ2H`, with real removable
media on a real machine. Every state, transition and lifecycle path below was
exercised by hand and behaved as specified:

| Exercised | Result |
|---|---|
| Default state on attach | Restricted — no drive letter |
| Read-only grant | Files readable and copyable *off* the device; writes refused by Windows |
| Read/write grant | Ordinary Windows access, including writing to the device |
| Timed access | Access ended on its own deadline |
| Revoke | Returned to Restricted immediately |
| Agent stopped | Device returned to normal, unmanaged Windows behaviour |
| Agent restarted | Persisted policy re-applied without server involvement |

Two defects were found by this acceptance rather than by the automated suites,
which is the argument for running it on hardware at all:

**A hub was classified from what was plugged into it.** The classifier gathered
driver services from the whole devnode subtree — and a hub's subtree is every
device on the bus. A root hub with a stick attached therefore collected
`USBSTOR` from that stick, classified as storage, and was disabled: the webcam,
fingerprint reader, Bluetooth radio and composite device on that hub all went
dark at the same instant, and the stick itself never appeared in inventory
because it now sat behind a dead hub. No automated test caught it because none
of them had a real device tree. Fixed by deciding hub-ness from the device's own
identity, before any storage rule, and by keeping the subtree walk inside one
physical device.

**Restricting a device destroyed the evidence that it was storage.** Disabling a
devnode unloads its driver and removes its child devnodes — the two signals the
classifier used to recognise removable storage. A restricted stick therefore
reappeared as an anonymous peripheral, dropped out of the console's storage
table, and could never be granted access again. Fixed by also recognising
storage from compatible IDs, which come from the device's own descriptors and
survive being disabled.

Both are covered by tests now, and the guard added alongside them refuses to
disable a hub regardless of classification — a guard that consulted the
classification it guards against would agree with it, including when it is
wrong.

The boundary stated earlier in this document is unchanged and was not
re-litigated by this acceptance: release is user-mode cleanup, so forced
termination, a crash, a bugcheck or power loss leave a disabled devnode disabled
until the agent next starts. No stronger claim is made.
