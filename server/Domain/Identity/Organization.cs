using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// A tenant boundary. Every device, platform user, policy and audit record belongs
/// to exactly one organization; queries are always scoped by it.
/// </summary>
/// <remarks>
/// The MVP deployment is single-organization, but carrying the column from the
/// first migration avoids a painful retrofit later and makes the scoping rule
/// explicit in every query from day one.
/// </remarks>
public sealed class Organization : AuditableEntity
{
    // EF Core materialisation constructor.
    private Organization()
    {
        Name = null!;
        Slug = null!;
    }

    public Organization(string name, string slug)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);
        Slug = NormalizeSlug(slug);
        IsActive = true;
    }

    public string Name { get; private set; }

    /// <summary>Lowercase, URL-safe, unique identifier used in APIs and tokens.</summary>
    public string Slug { get; private set; }

    public bool IsActive { get; private set; }

    public void Rename(string name) => Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 200);

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string NormalizeSlug(string slug)
    {
        var value = Guard.NotNullOrWhiteSpace(slug, nameof(slug), maxLength: 64).Trim().ToLowerInvariant();

        foreach (var c in value)
        {
            var allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '-' or '_';
            if (!allowed)
            {
                throw new ArgumentException(
                    $"Organization slug may contain only a-z, 0-9, '-' and '_'; found '{c}'.",
                    nameof(slug));
            }
        }

        return value;
    }
}
