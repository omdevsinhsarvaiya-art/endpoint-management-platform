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
