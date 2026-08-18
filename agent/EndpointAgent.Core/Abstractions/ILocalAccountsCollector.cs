using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads Windows local users, groups and membership.
/// </summary>
/// <remarks>
/// Read-only in Phase 4's first slice. The mutation counterpart (create user,
/// reset password, change membership) is a separate, later abstraction that will
/// be introduced together with its typed-task delivery, server-side permission
/// checks and elevated Windows integration tests — reads and writes deliberately
/// do not share an interface, so granting code access to "list users" can never
/// accidentally hand it "delete user".
/// </remarks>
public interface ILocalAccountsCollector
{
    ValueTask<InventoryLocalAccounts> CollectAsync(CancellationToken cancellationToken = default);
}
