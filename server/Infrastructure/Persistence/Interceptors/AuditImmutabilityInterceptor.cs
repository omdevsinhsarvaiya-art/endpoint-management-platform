using EndpointPlatform.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EndpointPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Refuses to save any change tracker state that would modify or delete an
/// existing audit entry.
/// </summary>
/// <remarks>
/// <para>
/// This is the innermost of three layers protecting the audit trail, and the
/// weakest — an attacker with database access bypasses it entirely. It exists so
/// that an <em>accidental</em> mutation (a careless <c>Update</c>, a cascade
/// delete, a bulk-fix script written against the DbContext) fails immediately with
/// a clear message during development rather than being caught later by the
/// database and reported as an opaque trigger error.
/// </para>
/// <para>
/// The controls that actually enforce immutability against a hostile caller are:
/// (1) the runtime database role holds only INSERT and SELECT on
/// <c>audit_log_entries</c>, and (2) a database trigger raises an exception on
/// UPDATE or DELETE regardless of role. Both are created by the initial migration.
/// See <c>docs/threat-model.md</c>.
/// </para>
/// </remarks>
public sealed class AuditImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Verify(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Verify(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Verify(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<AuditLogEntry>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new AuditTrailViolationException(entry.Entity.Id, entry.State.ToString());
            }
        }
    }
}

/// <summary>
/// Thrown when application code attempts to modify or delete a written audit entry.
/// </summary>
public sealed class AuditTrailViolationException(Guid auditEntryId, string attemptedState)
    : InvalidOperationException(
        $"The audit trail is append-only. Attempted to {attemptedState.ToUpperInvariant()} " +
        $"audit entry {auditEntryId}. Audit entries may only be inserted.")
{
    public Guid AuditEntryId { get; } = auditEntryId;

    public string AttemptedState { get; } = attemptedState;
}
