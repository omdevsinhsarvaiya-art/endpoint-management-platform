# ADR-0006: Testing stack — xUnit v2 + VSTest, Shouldly, Testcontainers

Status: accepted (Phase 0)

## Context

`dotnet test` working out of the box is a hard requirement. Two stack choices
needed recording:

1. **xUnit v3 vs v2.** xUnit v3 runs on Microsoft.Testing.Platform (MTP). On
   the .NET 10.0.400 SDK, `dotnet test` launches the MTP host in server mode
   (`--server dotnettestcli`), and neither xunit.v3 4.0.0 nor 3.2.2 completed
   that handshake in this repository: 4.0.0 reported "Zero tests ran" while
   the same assembly executed directly passed all tests; 3.2.2 printed its own
   help text. A tooling-integration failure, not a test failure — but one that
   breaks the primary developer workflow.
2. **Assertions.** FluentAssertions 8+ moved to the Xceed licence, which
   requires a paid commercial licence for company use.

## Decision

- xUnit **2.9.x** with `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`
  (the VSTest path `dotnet test` supports natively). Common settings live in
  `build/Tests.props`.
- **Shouldly** (BSD-3-Clause) for assertions; NSubstitute for mocking.
- **Testcontainers** for integration tests against real PostgreSQL, image
  pinned to the same tag as `infra/docker-compose.yml`.

## Consequences

- No xUnit v3 features (TestContext, MTP-native runs). Acceptable; nothing in
  the suite needs them.
- Revisit once the SDK/xunit.v3 handshake stabilises — the migration is
  mechanical (namespaces and csproj properties).
- No licence exposure from FluentAssertions.
