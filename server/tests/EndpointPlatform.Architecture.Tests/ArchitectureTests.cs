using System.Reflection;

namespace EndpointPlatform.Architecture.Tests;

/// <summary>
/// Enforces the dependency rules stated in docs/architecture.md.
/// </summary>
/// <remarks>
/// Layering rules that live only in a document rot silently: one convenient
/// <c>using</c> and the domain depends on EF Core forever. These tests make the
/// rules part of the build.
/// </remarks>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Domain.Common.Entity).Assembly;
    private static readonly Assembly Contracts = typeof(Contracts.AgentProtocol).Assembly;
    private static readonly Assembly Infrastructure =
        typeof(Infrastructure.Persistence.EndpointPlatformDbContext).Assembly;
    private static readonly Assembly AdminApi = typeof(Api.Program).Assembly;
    private static readonly Assembly AgentApi = typeof(AgentApi.Program).Assembly;

    [Fact]
    public void Domain_references_no_infrastructure_framework()
    {
        // The domain must stay persistence- and transport-agnostic: pure entities
        // and rules, testable with nothing but xUnit.
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Microsoft.AspNetCore",
            "Serilog",
            "System.Data.SqlClient",
        ];

        AssertNoReference(Domain, forbidden);
    }

    [Fact]
    public void Domain_references_nothing_in_this_solution()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        references.ShouldNotContain("EndpointPlatform.Infrastructure");
        references.ShouldNotContain("EndpointPlatform.Api");
        references.ShouldNotContain("EndpointPlatform.AgentApi");
        references.ShouldNotContain("EndpointPlatform.Contracts");
    }

    [Fact]
    public void Contracts_are_dependency_free()
    {
        // The agent compiles against Contracts. Every dependency added here is
        // shipped to and loaded on every managed endpoint, so the bar is "none".
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Microsoft.AspNetCore",
            "Serilog",
            "EndpointPlatform.Domain",
            "EndpointPlatform.Infrastructure",
        ];

        AssertNoReference(Contracts, forbidden);
    }

    [Fact]
    public void The_admin_api_does_not_reference_the_agent_api_or_vice_versa()
    {
        // Separate trust boundaries: neither host may be able to reach the other's
        // types, or endpoints could quietly migrate across the boundary.
        AdminApi.GetReferencedAssemblies().Select(a => a.Name)
            .ShouldNotContain("EndpointPlatform.AgentApi");

        AgentApi.GetReferencedAssemblies().Select(a => a.Name)
            .ShouldNotContain("EndpointPlatform.Api");
    }

    [Fact]
    public void Infrastructure_does_not_reference_either_api_host()
    {
        var references = Infrastructure.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        references.ShouldNotContain("EndpointPlatform.Api");
        references.ShouldNotContain("EndpointPlatform.AgentApi");
    }

    [Fact]
    public void Domain_types_do_not_expose_ef_core_attributes()
    {
        // Persistence mapping belongs in IEntityTypeConfiguration classes in
        // Infrastructure, not in data annotations sprinkled over the domain.
        foreach (var type in Domain.GetTypes())
        {
            foreach (var attribute in type.GetCustomAttributesData())
            {
                var ns = attribute.AttributeType.Namespace ?? string.Empty;

                ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal).ShouldBeFalse(
                    $"{type.FullName} carries EF Core attribute {attribute.AttributeType.Name}; " +
                    "mapping belongs in Infrastructure configurations.");

                ns.StartsWith("System.ComponentModel.DataAnnotations.Schema", StringComparison.Ordinal)
                    .ShouldBeFalse(
                        $"{type.FullName} carries schema attribute {attribute.AttributeType.Name}; " +
                        "mapping belongs in Infrastructure configurations.");
            }
        }
    }

    private static void AssertNoReference(Assembly assembly, string[] forbiddenPrefixes)
    {
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

        foreach (var forbidden in forbiddenPrefixes)
        {
            references.ShouldNotContain(
                r => r == forbidden || r.StartsWith(forbidden + ".", StringComparison.Ordinal),
                $"{assembly.GetName().Name} must not reference {forbidden}");
        }
    }
}
