using EndpointAgent.Core.Enrollment;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Persists the in-flight enrollment request across service restarts and reboots.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IDeviceCredentialStore"/> on purpose: the two hold
/// different things with different lifetimes, and only one of them ever exists at a
/// time. A machine either has a pending request or a credential; conflating them
/// would make "enrolled" and "waiting to enrol" the same file and invite one to
/// overwrite the other.
/// </para>
/// <para>
/// The Windows implementation protects this with DPAPI at LocalMachine scope in the
/// same ACL-hardened state directory as the credential, because
/// <see cref="PendingEnrollmentState.RequestSecret"/> is the proof of possession that
/// redeems an approved enrollment.
/// </para>
/// </remarks>
public interface IEnrollmentStateStore
{
    /// <summary>
    /// The pending request, or null when there is none. Returns null rather than
    /// throwing when the stored value is unreadable — a corrupt or foreign state file
    /// means "no usable request", and the agent should start a new one rather than
    /// fail to start.
    /// </summary>
    ValueTask<PendingEnrollmentState?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the request. Must be durable before the request is sent.</summary>
    ValueTask SaveAsync(PendingEnrollmentState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the request. Called once a credential has been stored, and when a
    /// request is rejected or expired.
    /// </summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
