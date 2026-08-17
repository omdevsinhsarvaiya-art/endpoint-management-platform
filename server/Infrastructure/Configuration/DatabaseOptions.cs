using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Configuration;

/// <summary>
/// PostgreSQL connection settings.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectionString"/> carries a credential and is therefore never
/// written to a log, never returned by an API and never committed to source
/// control. In development it comes from <c>.env</c> / user-secrets; in deployment
/// it comes from the environment or a secret store. The value shipped in
/// <c>appsettings.json</c> is intentionally empty so a missing configuration fails
/// loudly at startup instead of silently falling back to a default credential.
/// </para>
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Database:ConnectionString is not configured. Set ENDPOINTPLATFORM_Database__ConnectionString " +
        "or copy infra/.env.example to infra/.env. See docs/development.md.")]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Seconds an individual command may run before Npgsql cancels it.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Transient-failure retry count. EF's execution strategy only retries errors
    /// Npgsql classifies as transient, so this cannot mask a genuine constraint
    /// violation.
    /// </summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; init; } = 5;

    [Range(1, 120)]
    public int MaxRetryDelaySeconds { get; init; } = 10;

    /// <summary>
    /// Enables EF Core parameter values in logs and exception messages. Off by
    /// default and refused outright outside Development, because parameter values
    /// include password hashes, tokens and personal data.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; init; }

    /// <summary>
    /// Applies pending migrations during host startup. Convenient for local
    /// development; in deployment the dedicated migration runner
    /// (<c>EndpointPlatform.Migrations</c>) is used instead so that schema changes
    /// run once, under a different database role, before any API instance starts.
    /// </summary>
    public bool MigrateOnStartup { get; init; }
}
