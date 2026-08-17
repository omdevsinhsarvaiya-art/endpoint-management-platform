# ADR-0007: Target .NET 10 instead of the specified .NET 8

Status: accepted (Phase 0) — explicit stakeholder decision

## Context

The project specification names ASP.NET Core 8 / .NET 8. The development
machine has only the .NET 10.0.400 SDK and .NET 10 runtimes installed — no
.NET 8 SDK or runtime. Installing the .NET 8 SDK side-by-side was offered; the
stakeholder chose to build on what is installed.

## Decision

All projects target `net10.0` (agent: `net10.0-windows`), with package
versions aligned to the 10.x train (EF Core 10, Npgsql provider 10, Serilog
10-compatible). `global.json` pins the 10.0.x SDK.

## Consequences

- .NET 10 is the current LTS (November 2025), supported until November 2028 —
  longer than .NET 8 (November 2026).
- APIs used in this codebase are compatible with the .NET 8 shapes; a
  downgrade, if ever required, is mostly TFM + package-version mechanical
  work. `Guid.CreateVersion7()` (used for entity ids) is .NET 9+; a .NET 8
  downgrade would need a v7 GUID polyfill.
- Documentation and deployment instructions say .NET 10 everywhere; the
  original spec's ".NET 8" should be read as "current LTS".
