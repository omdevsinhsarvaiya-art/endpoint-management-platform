using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests: these run the real provider against the machine's
/// actual WMI repository. They therefore assert invariants (non-empty, stable,
/// sane shape) rather than specific values, which differ per machine.
/// </summary>
public sealed class WindowsSystemInfoProviderTests
{
    private static WindowsSystemInfoProvider CreateProvider() =>
        new(NullLogger<WindowsSystemInfoProvider>.Instance);

    [Fact]
    public void Host_name_matches_the_environment()
    {
        CreateProvider().GetHostName().ShouldBe(Environment.MachineName);
    }

    [Fact]
    public async Task Operating_system_description_is_populated_and_mentions_windows()
    {
        var provider = CreateProvider();

        var description = await provider.GetOperatingSystemDescriptionAsync(CancellationToken.None);

        description.ShouldNotBeNullOrWhiteSpace();
        description.ShouldContain("Windows", Case.Insensitive);
    }

    [Fact]
    public async Task Operating_system_description_is_cached_across_calls()
    {
        var provider = CreateProvider();

        var first = await provider.GetOperatingSystemDescriptionAsync(CancellationToken.None);
        var second = await provider.GetOperatingSystemDescriptionAsync(CancellationToken.None);

        second.ShouldBeSameAs(first, "the value cannot change while the process runs, so it must be cached");
    }

    [Fact]
    public async Task Machine_identifier_is_stable_across_calls()
    {
        var provider = CreateProvider();

        var first = await provider.GetMachineIdentifierAsync(CancellationToken.None);
        var second = await provider.GetMachineIdentifierAsync(CancellationToken.None);

        first.ShouldNotBeNullOrWhiteSpace();
        second.ShouldBe(first, "duplicate-device detection depends on this value being stable");
    }

    [Fact]
    public async Task Machine_identifier_is_never_the_all_zero_placeholder()
    {
        var provider = CreateProvider();

        var identifier = await provider.GetMachineIdentifierAsync(CancellationToken.None);

        identifier.ShouldNotBe("00000000-0000-0000-0000-000000000000",
            "the provider must fall back rather than return the SMBIOS null UUID");
    }
}

