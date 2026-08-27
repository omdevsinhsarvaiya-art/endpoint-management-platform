namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The accounts this agent has elevated, remembered across restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is necessary rather than convenient.</b> Reconciliation has to
/// remove administrator rights from accounts whose authorization has ended. It
/// must not remove them from accounts that were administrators before this
/// platform ever touched the machine. Windows cannot tell the two apart --
/// membership of Administrators looks identical either way -- so the only way to
/// know which rights are ours to withdraw is to have written it down when we
/// granted them.
/// </para>
/// <para>
/// Without this the agent would face two equally wrong options: remove every
/// administrator not currently authorized, which would strip a machine's real
/// administrators the first time it reconciled; or remove none, which would
/// leave every expired elevation in force forever.
/// </para>
/// <para>
/// Stored as plain JSON, for the same reason as the USB restriction ledger: its
/// job is to be readable at the worst possible moment. A sealed file that cannot
/// be decrypted after a re-image would strand elevated accounts with no record
/// of which ones to lower, and robustness is the security property that matters
/// for this file. Tampering with it cannot widen access -- it is only ever read
/// to decide what to <em>remove</em>, and adding an entry causes at most an
/// unnecessary de-elevation, which the safety rules still refuse to perform if it
/// would strand the machine.
/// </para>
/// </remarks>
public interface IElevationLedger
{
    /// <summary>
    /// SIDs this agent has elevated and has not yet lowered. Returns empty rather
    /// than throwing when the record is missing or damaged.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyCollection<string> sids, CancellationToken cancellationToken = default);
}
