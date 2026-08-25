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

Access, when granted, is always:

- **read-only** — there is no state in this system that permits writing to a
  removable device, and no payload, API call or role can express one;
- **time-boxed** — an absolute deadline between 5 minutes and 24 hours, chosen
  at grant time and never extended;
- **per device** — keyed to one Windows device instance ID on one endpoint;
- **justified and audited** — a reason is required, and the grant, its
  revocation and its expiry are each an audit record.

Non-storage peripherals — keyboards, mice, hubs, cameras, network adapters —
are inventoried and never restricted. Disabling an input device would lock the
user out of their own machine.

## How it is enforced on Windows

Two mechanisms, both per-device, both documented public API, neither of them a
shell command or a kernel driver (ADR-0005):

| State | Mechanism | Effect |
|---|---|---|
| Restricted | SetupAPI `DIF_PROPERTYCHANGE` / `DICS_DISABLE` on the device instance | The device does not start. No volume, no drive letter, nothing to open. |
| Read-only | Device enabled, then `IOCTL_DISK_SET_DISK_ATTRIBUTES` with `DISK_ATTRIBUTE_READ_ONLY` on each disk beneath it | Windows itself refuses writes, creates, renames and deletes. |

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
as failed unless the disk reports read-only. And if read-only cannot be applied
for any reason, the device is restricted instead — never left enabled and
writable.

## How a grant travels

```
administrator grants read-only access (usb.manage, justification, duration)
        ↓ audited; device marked ReadOnly with an absolute deadline
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

## What this does NOT do

Stated explicitly, because a security control that is oversold is worse than
one that is absent.

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

Enforcement itself changes real hardware state and is verified on a designated
test endpoint, never on a developer machine or a CI runner.
