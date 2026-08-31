namespace EndpointAgent.Core.Abstractions;

/// <summary>How a recovery-password read ended.</summary>
public enum RecoveryPasswordReadStatus
{
    Success = 0,

    /// <summary>Windows returned a failure code: elevation, policy, or volume state.</summary>
    Refused = 1,

    /// <summary>The volume or protector no longer exists.</summary>
    ProtectorGone = 2,

    /// <summary>Windows returned something that is not a recovery password.</summary>
    Malformed = 3,
}

/// <summary>
/// The outcome of one read. Carries the password only on success.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ToString"/> is overridden and never renders the password.</b> A
/// result object is exactly the kind of value that ends up in a structured log
/// through an interpolated message or a captured scope, so the type refuses to
/// render it even when asked.
/// </para>
/// <para>
/// The password is a managed string because that is what the Windows API returns;
/// see the remarks on the reader for what that costs.
/// </para>
/// </remarks>
public sealed record RecoveryPasswordReadResult(
    RecoveryPasswordReadStatus Status,
    string? RecoveryPassword,
    string? ProtectorId)
{
    public bool Success => Status == RecoveryPasswordReadStatus.Success
        && !string.IsNullOrWhiteSpace(RecoveryPassword);

    public static RecoveryPasswordReadResult Failed(RecoveryPasswordReadStatus status, string? protectorId = null) =>
        new(status, null, protectorId);

    public override string ToString() =>
        $"RecoveryPasswordReadResult(Status: {Status}, ProtectorId: {ProtectorId}, RecoveryPassword: <redacted>)";
}

/// <summary>
/// Reads the numerical recovery password for one specific protector.
/// </summary>
/// <remarks>
/// <para>
/// <b>This reverses the platform's original decision (M13, J-4) that the agent
/// would never call <c>GetKeyProtectorNumericalPassword</c>.</b> The original
/// rationale stands on its own terms and is preserved in
/// <c>docs/threat-model.md</c>: an agent that cannot read a recovery password
/// cannot leak one. The reversal was made deliberately, to escrow recovery
/// credentials without an administrator transcribing 48 digits per machine, and it
/// materially raises the aggregation and exfiltration risk the threat model now
/// records.
/// </para>
/// <para>
/// The narrow contract is the mitigation. An implementation reads the password for
/// the protector it is given and no other; there is no method here that enumerates
/// passwords, and callers reach this interface only after eligibility, pinning and
/// deduplication have all passed -- so on an ineligible machine the call is never
/// made and the credential is never materialised.
/// </para>
/// </remarks>
public interface IRecoveryPasswordReader
{
    /// <summary>
    /// Reads the recovery password for <paramref name="keyProtectorId"/> on
    /// <paramref name="volumeDeviceIdentifier"/>.
    /// </summary>
    /// <remarks>
    /// Returns a status rather than throwing for expected refusals. An exception
    /// carrying Windows' own message is a place a credential could surface, and
    /// "Windows said no" is an ordinary outcome here, not an exceptional one.
    /// </remarks>
    Task<RecoveryPasswordReadResult> ReadAsync(
        string volumeDeviceIdentifier,
        string keyProtectorId,
        CancellationToken cancellationToken = default);
}
