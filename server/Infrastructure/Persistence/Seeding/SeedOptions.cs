using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Persistence.Seeding;

/// <summary>Settings for the idempotent reference-data seeder.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Display name of the default organization created on an empty database.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string DefaultOrganizationName { get; init; } = "Default Organization";

    /// <summary>URL-safe slug of the default organization. Stable across restarts.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z0-9_-]+$")]
    [MaxLength(64)]
    public string DefaultOrganizationSlug { get; init; } = "default";
}
