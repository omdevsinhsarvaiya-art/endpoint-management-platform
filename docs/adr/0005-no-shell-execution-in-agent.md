# ADR-0005: The agent never executes shell commands

Status: accepted (Phase 0)

## Context

The agent runs as LocalSystem. The most dangerous pattern privileged agents
adopt is composing command strings ("net user " + name + " ...") and handing
them to cmd/PowerShell: every string that touches that path becomes a
potential privileged injection, and auditing "what did we actually run"
becomes string archaeology.

## Decision

The agent performs Windows work exclusively through APIs — Win32/P-Invoke,
WMI/CIM with fixed query text, System.DirectoryServices.AccountManagement,
registry APIs — which take typed parameters and have no command line to
inject into.

The rule is enforced structurally, not by review: the agent assemblies do not
reference `System.Diagnostics.Process` or the PowerShell SDK **at all**
(`AgentSafetyTests` fails the build otherwise). WMI usage keeps query text
constant — no interpolation of runtime values into WQL.

When Phase 10 introduces approved-script execution, it will be a reviewed,
narrow call site behind the signed-script pipeline (hash + signature +
recorded approval), and the enforcement test will be tightened to allow
exactly that call site rather than removed.

## Consequences

- Local user/group management (Phase 4) must use account-management APIs even
  where a shell one-liner would be shorter. This is intended friction.
- No PowerShell SDK dependency keeps the agent's footprint and attack surface
  small.
- Anything genuinely impossible without a process launch is deferred to the
  Phase 10 pipeline rather than snuck in early.

## Amendment (Phase 9): process/service control

Phase 9 introduces service control (`ServiceController`) and process termination
(`Process.GetProcessById().Kill()` with an expected-image guard). These are NOT
the pattern ADR-0005 forbids: they take typed arguments and have no command line
to inject into. The enforcement test was therefore made *precise* rather than
loosened:

- `EndpointAgent.Core` still references no process API at all.
- A source scan asserts **`Process.Start` appears in no agent source file** -
  that is the actual shell/launch vector. `Kill`/`GetProcessById`/`GetProcesses`
  and `ServiceController` are permitted.
- The PowerShell-SDK ban is unchanged.

`Process.Start` (for approved-script execution) remains forbidden until the
signed-script pipeline exists, at which point the scan is tightened to allow
exactly that one reviewed call site.

## Amendment (Phase 11): MSI installation via the Windows Installer service

Phase 11 gives the agent a real software-install capability. This is the one
capability that appears to reopen the door ADR-0005 closes, so the boundary is
drawn deliberately:

- Installation is driven by **`MsiInstallProduct` (msi.dll)**, product detection
  by **`MsiQueryProductState`**, and there is **no process launch and no shell**.
  `Process.Start` remains absent from every agent source file (the
  `AgentSafetyTests` scan is unchanged and still passes). MSI is a data format
  consumed by a Windows service, not a command line the agent composes.
- The capability is **closed to signed MSI**. The package type enum has one
  member (`WindowsInstaller`); there is no `.exe`, no script, no arbitrary
  installer. Widening it is a reviewed code change, not configuration.
- **Two independent pins** gate every install, both verified on the agent:
  1. the content **SHA-256**, checked against the pin in the task payload before
     a byte reaches the installer; and
  2. the **Authenticode signer**, verified via `WinVerifyTrust` plus a signer
     -subject match, refusing an unsigned or untrusted file outright.
  A server tricked into serving the wrong bytes, or a network tamperer, cannot
  get anything installed: the hash check fails first, the signature check second.
- Installs are **idempotent by ProductCode** and reboots are **suppressed** (an
  install that wants one is reported, never performed). The privileged install
  runs as LocalSystem; the Admin API only ever stores content and queues intent,
  both audited (`software.deploy`, marked high-risk).

This does not reintroduce arbitrary execution: the agent cannot be made to run
anything other than a hash-pinned, signature-verified MSI that an administrator
holding `software.deploy` explicitly registered and deployed. The
`Process.Start`/PowerShell bans, and their enforcement test, are unchanged.

## Amendment (Phase 16): application removal via the Windows Installer service and the AppX deployment engine

Phase 16 lets an operator remove an installed application: one
`RemoveApplication` task stops it exactly as Force Stop does, then uninstalls
it. Removal is the mirror image of the Phase 11 install, and the boundary is
drawn the same way:

- A Windows Installer product is removed by **`MsiConfigureProductEx` (msi.dll)**
  with `INSTALLSTATE_ABSENT` and the agent-authored property string
  `REBOOT=ReallySuppress`. Its registered name and install location are read by
  **`MsiGetProductInfo`**, its membership of an upgrade code by
  **`MsiEnumRelatedProducts`**, and its presence before and after by
  `MsiQueryProductState`. An MSIX/AppX package is removed by **COM activation of
  `Windows.Management.Deployment.PackageManager`** and its
  **`RemovePackageWithOptionsAsync`** (`RemoveForAllUsers`), reached through raw
  Windows Runtime activation with hand-declared interfaces — no projection and
  no target-framework change. These are the permitted calls; there are no others.
- **No command line is composed and no process is launched.** `Process.Start`
  remains absent from every agent source file (the `AgentSafetyTests` scan is
  unchanged and still passes). The product code and the package full name are
  values the platform's own inventory recorded — typed arguments to a Windows
  service, not strings the agent assembles into anything.
- A product's own **custom actions run inside msiexec as SYSTEM**, exactly as
  they did when the product was installed under Phase 11. Uninstall runs the
  vendor's code with the privilege install had; that is the Windows Installer
  contract, accepted in Phase 11 and unchanged here.
- **EXE uninstallers stay deferred.** An application registered by a
  traditional installer is removed by running its `UninstallString` — a program
  with a vendor-supplied command line, the exact vector this ADR forbids. Such
  rows are reported as not removable, with the reason; the signed-script
  pipeline remains the route for them, and nothing here anticipates it.
- **Self-protection is decided on the endpoint.** The agent refuses to remove a
  product that belongs to its own upgrade code
  (`{8F3C1D92-6B74-4A5E-9D21-7C4E8B0F5A63}`), carries its product name
  (`Endpoint Platform Agent`), or is installed into, or around, its own
  directory; and refuses a package whose publisher is Windows' own
  (`cw5n1h2txyewy`) or whose registered root lies under the Windows directory.
  The server applies the same rules before queueing; the agent does not rely
  on that, because an agent that removed itself would leave the device
  unmanaged and the task unreported.

Removal is **idempotent** (an absent product or package is reported as already
removed), reboots are **suppressed** and reported, and the Admin API only ever
queues intent, audited under `software.deploy` and marked high-risk. The
`Process.Start`/PowerShell bans, and their enforcement test, are unchanged.
