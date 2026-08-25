namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Remembers which device instances this agent has applied state to, so that all
/// of it can be undone when the agent stops enforcing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a ledger rather than "re-enable whatever is attached".</b> Two reasons,
/// and the second is the one that matters. First, releasing only what we changed
/// means an administrator who disabled a device by hand in Device Manager keeps
/// their decision — we undo our own work, not theirs. Second, and more
/// importantly, a device that has been restricted and then unplugged is
/// <em>still</em> disabled: <c>CONFIGFLAG_DISABLED</c> lives in the registry under
/// the device's instance key and stays there whether or not the stick is in the
/// port. Enumeration cannot find it to release it. Without a written record of
/// what we disabled, uninstalling the product would leave those devices disabled
/// forever, and the damage would only surface the next time somebody plugged one
/// in.
/// </para>
/// <para>
/// <b>Why this is not sealed with DPAPI</b>, unlike the grant store. This file's
/// job is to be readable at the worst possible moment — during uninstall, on a
/// machine that may have been re-imaged or had its DPAPI master key rotated. A
/// grant set that cannot be unsealed fails safe, because "no grants" means
/// "restrict everything". A ledger that cannot be read fails <em>unsafe</em> in
/// the sense that matters here: it leaves the user's hardware disabled with no
/// record of how to restore it. Robustness is the security property for this
/// file, so it is plain JSON.
/// </para>
/// <para>
/// Tampering with it gains an attacker nothing. The ledger never widens access on
/// its own — it is only ever read to undo enforcement during shutdown or
/// uninstall, both of which already release everything. Someone able to write to
/// the agent's state directory is already an administrator, and an administrator
/// can stop the service outright.
/// </para>
/// </remarks>
public interface IUsbRestrictionLedger
{
    /// <summary>
    /// The device instances this agent currently has state applied to. Returns
    /// empty rather than throwing when the record is missing or damaged.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default);
}
