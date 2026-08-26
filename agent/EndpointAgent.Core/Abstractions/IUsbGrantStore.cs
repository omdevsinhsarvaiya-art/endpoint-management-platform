namespace EndpointAgent.Core.Abstractions;

/// <summary>One administrator-issued grant, as the endpoint remembers it.</summary>
/// <param name="InstanceId">The exact device this covers.</param>
/// <param name="Policy">
/// The level granted. Defaults to <see cref="UsbEnforcedState.ReadOnly"/> so
/// that a grant deserialised from a cache written by an older agent — which
/// had no such field — is read as the narrower level rather than as write
/// access nobody granted.
/// </param>
/// <param name="ExpiresAt">
/// Absolute UTC deadline. The agent compares this against its own clock, which
/// is what makes a grant lapse on time on a machine that never reaches the
/// server again.
/// </param>
public sealed record UsbGrantRecord(
    string InstanceId,
    DateTimeOffset ExpiresAt,
    UsbEnforcedState Policy = UsbEnforcedState.ReadOnly);

/// <summary>The endpoint's cached copy of the policy the server issued.</summary>
/// <param name="IssuedAt">
/// When the server built this policy. Used for last-writer-wins so a delayed
/// task cannot resurrect a grant that was revoked after it was queued.
/// </param>
public sealed record UsbGrantSet(IReadOnlyList<UsbGrantRecord> Grants, DateTimeOffset IssuedAt)
{
    public static readonly UsbGrantSet Empty = new([], DateTimeOffset.MinValue);
}

/// <summary>
/// Persists the current grant set across service restarts and reboots.
/// </summary>
/// <remarks>
/// <para>
/// Persistence exists so that a machine which reboots — or an agent which
/// restarts — does not have to reach the server before it knows what to do.
/// Its failure mode is the safe one: an unreadable, missing or tampered store
/// yields <see cref="UsbGrantSet.Empty"/>, which restricts everything.
/// </para>
/// <para>
/// The Windows implementation seals the file with DPAPI at LocalMachine scope in
/// the agent's hardened state directory, so a standard user can neither read nor
/// forge it. It is <em>not</em> proof against a local administrator: someone
/// holding admin on the endpoint can stop the service or re-enable the device in
/// Device Manager regardless of what this file says. That limit is inherent to a
/// user-mode agent and is documented rather than papered over.
/// </para>
/// </remarks>
public interface IUsbGrantStore
{
    ValueTask<UsbGrantSet> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(UsbGrantSet grants, CancellationToken cancellationToken = default);
}
