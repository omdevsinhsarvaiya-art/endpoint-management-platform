namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Secure storage for the long-lived device credential established at enrollment.
/// </summary>
/// <remarks>
/// <para>
/// The credential is what proves this machine's identity to the Agent API for the
/// rest of its life, so its storage is a security boundary in its own right. The
/// Windows implementation protects it with DPAPI at machine scope and writes it to
/// a directory ACL'd to SYSTEM and Administrators, meaning a standard user on the
/// endpoint cannot read it and it cannot be copied to another machine and reused.
/// </para>
/// <para>
/// The interface deliberately has no "read the raw secret" method that returns it
/// to arbitrary callers as a plain string beyond what signing requires, and no
/// implementation may log the value. Phase 1 fills in the concrete shape of the
/// credential; Phase 0 establishes only the abstraction and its contract.
/// </para>
/// </remarks>
public interface IDeviceCredentialStore
{
    /// <summary>True when this machine has completed enrollment.</summary>
    ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the stored credential. Used when a device is retired or re-enrolled.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
