namespace EndpointPlatform.Domain.Common;

/// <summary>
/// An entity that records when it was created and last modified.
/// </summary>
/// <remarks>
/// The timestamps are written by the persistence layer
/// (<c>AuditableEntityInterceptor</c>) from an injected <see cref="TimeProvider"/>,
/// never by calling <c>DateTimeOffset.UtcNow</c> inside the domain. That keeps the
/// domain deterministic under test and guarantees one consistent timestamp for
/// every entity touched by a single <c>SaveChanges</c>.
/// </remarks>
public abstract class AuditableEntity : Entity
{
    protected AuditableEntity()
    {
    }

    protected AuditableEntity(Guid id)
        : base(id)
    {
    }

    // Private setters: EF Core materialises through them, application code cannot.
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Applied by the persistence layer. Exposed as an explicit method rather than
    /// public setters so that application code cannot silently forge timestamps.
    /// </summary>
    public void StampCreated(DateTimeOffset now)
    {
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Applied by the persistence layer on update.</summary>
    public void StampUpdated(DateTimeOffset now) => UpdatedAt = now;
}
