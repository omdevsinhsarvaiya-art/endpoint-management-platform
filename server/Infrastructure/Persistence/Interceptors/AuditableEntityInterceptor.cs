using EndpointPlatform.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EndpointPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps <c>CreatedAt</c> / <c>UpdatedAt</c> on every <see cref="AuditableEntity"/>
/// touched by a save.
/// </summary>
/// <remarks>
/// A single <see cref="TimeProvider"/> read per save gives every entity in the same
/// transaction an identical timestamp, so "these rows changed together" is
/// expressible as an equality rather than a range. Taking the clock from DI also
/// keeps tests deterministic — no <c>DateTimeOffset.UtcNow</c> anywhere in the
/// persistence path.
/// </remarks>
public sealed class AuditableEntityInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.StampCreated(now);
                    break;

                case EntityState.Modified:
                    entry.Entity.StampUpdated(now);
                    // Never let an update rewrite the creation time.
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    break;

                default:
                    break;
            }
        }
    }
}
