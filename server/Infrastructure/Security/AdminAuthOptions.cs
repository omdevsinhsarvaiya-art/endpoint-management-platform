using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>Authentication policy for the Admin API.</summary>
public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    /// <summary>Absolute session lifetime, hours. No sliding renewal in v1.</summary>
    [Range(1, 72)]
    public int SessionLifetimeHours { get; init; } = 12;

    /// <summary>Failed sign-ins before the account locks.</summary>
    [Range(3, 20)]
    public int LockoutThreshold { get; init; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>Sign-in attempts allowed per client address per minute.</summary>
    [Range(1, 10_000)]
    public int LoginAttemptsPerMinutePerAddress { get; init; } = 10;
}
