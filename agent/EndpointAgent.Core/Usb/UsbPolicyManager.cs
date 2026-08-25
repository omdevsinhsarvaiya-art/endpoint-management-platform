using System.Collections.Concurrent;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Usb;

/// <summary>
/// Keeps this machine's USB storage in the state the platform says it should be
/// in, and tells the server what it actually managed to do.
/// </summary>
/// <remarks>
/// <para>
/// One rule governs everything here: <b>a storage device with no live grant is
/// restricted.</b> Not "restricted once the server says so" — restricted by
/// default, including on a machine that has never enrolled, cannot reach the
/// network, or has just booted with a stick already in the port. Access is the
/// exception that requires a positive, unexpired, administrator-issued grant for
/// that exact device instance ID.
/// </para>
/// <para>
/// Every failure path therefore lands on Restricted. An unreadable grant store,
/// an unreachable server, an expired cache, a task that never arrived, a
/// malformed payload: all of them mean "no live grant", which means restricted.
/// The only way to widen access is for <see cref="ApplyPolicy"/> to receive a
/// grant that is genuinely still in date.
/// </para>
/// <para>
/// Non-storage devices are inventoried and never touched. Disabling a USB
/// keyboard or mouse would lock the user out of their own machine, and no
/// security benefit would justify it.
/// </para>
/// </remarks>
public sealed class UsbPolicyManager(
    IUsbDeviceEnumerator enumerator,
    IUsbPolicyEnforcer enforcer,
    IUsbGrantStore grantStore,
    TimeProvider timeProvider,
    ILogger<UsbPolicyManager> logger)
{
    private readonly IUsbDeviceEnumerator _enumerator = enumerator
        ?? throw new ArgumentNullException(nameof(enumerator));

    private readonly IUsbPolicyEnforcer _enforcer = enforcer
        ?? throw new ArgumentNullException(nameof(enforcer));

    private readonly IUsbGrantStore _grantStore = grantStore
        ?? throw new ArgumentNullException(nameof(grantStore));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<UsbPolicyManager> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Serialises the read-modify-enforce path so two reconciles cannot fight
    /// over the same device's state.
    /// </summary>
    /// <remarks>
    /// Genuinely contended: <see cref="ApplyPolicyAsync"/> is called by the task
    /// executor on the heartbeat loop's thread, while <see cref="ReconcileAsync"/>
    /// is called by the USB monitor loop on its own.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Grants currently believed live. Replaced wholesale, never mutated, so a
    /// reader outside the gate always sees one coherent set rather than a
    /// half-updated one.
    /// </summary>
    private volatile UsbGrantSet _grants = UsbGrantSet.Empty;

    private bool _loaded;

    /// <summary>
    /// What the last enforcement attempt achieved, keyed by instance ID.
    /// </summary>
    /// <remarks>
    /// Concurrent because <see cref="BuildReport"/> reads it without taking the
    /// gate — it must stay cheap and must never block the reporting path behind
    /// an in-flight enforcement, which can spend seconds waiting for a disk to
    /// appear.
    /// </remarks>
    private readonly ConcurrentDictionary<string, UsbEnforcementResult> _lastResult =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the cached policy with one the server issued, then reconciles.
    /// </summary>
    /// <remarks>
    /// Older policies are ignored by <paramref name="issuedAt"/>. Without that, a
    /// task queued before a revocation but delivered after it — entirely possible
    /// for a machine that was offline — would reinstate the access that was
    /// revoked. Rejecting stale policies makes late delivery harmless.
    /// </remarks>
    /// <returns>The devices whose state changed, for logging.</returns>
    public async Task<UsbReconcileOutcome> ApplyPolicyAsync(
        IReadOnlyList<UsbGrantRecord> grants,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);

            if (issuedAt < _grants.IssuedAt)
            {
                _logger.LogInformation(
                    "Ignoring a USB policy issued {IssuedAt} because a newer one issued {Current} is already "
                    + "in force. A late policy must not reinstate access that has since been revoked.",
                    issuedAt, _grants.IssuedAt);

                return await ReconcileLockedAsync(cancellationToken);
            }

            _grants = new UsbGrantSet(grants, issuedAt);
            await _grantStore.SaveAsync(_grants, cancellationToken);

            return await ReconcileLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings every attached storage device into line with the cached policy.
    /// </summary>
    /// <remarks>
    /// Safe and cheap to call as often as needed — on device arrival, on a timer,
    /// at startup, after a policy push. Enforcement calls are idempotent, so
    /// reconciling a machine that is already correct changes nothing.
    /// </remarks>
    public async Task<UsbReconcileOutcome> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return await ReconcileLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the report for the server: every attached device, plus what this
    /// agent is actually enforcing on each.
    /// </summary>
    public UsbReport BuildReport()
    {
        var now = _timeProvider.GetUtcNow();
        var devices = SafeEnumerate();

        var entries = devices.Select(device =>
        {
            string? enforced = null;
            string? error = null;

            if (device.Class == UsbClass.Storage)
            {
                // Report the state we last succeeded in applying, not the state we
                // asked for. If enforcement failed, the server hears about the
                // failure and the console shows the device as unenforced.
                if (_lastResult.TryGetValue(device.InstanceId, out var result) && !result.Succeeded)
                {
                    error = result.Error;
                }
                else
                {
                    enforced = Desired(device.InstanceId, now) == UsbEnforcedState.ReadOnly
                        ? nameof(UsbEnforcedState.ReadOnly)
                        : nameof(UsbEnforcedState.Restricted);
                }
            }

            return new UsbDeviceReport(
                device.InstanceId,
                device.Class.ToString(),
                device.VendorId,
                device.ProductId,
                device.SerialNumber,
                device.Manufacturer,
                device.Product,
                device.HardwareIds,
                IsConnected: true,
                enforced,
                error);
        }).ToList();

        return new UsbReport(entries, now);
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            _grants = await _grantStore.LoadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail closed and say so. An unreadable cache is not a reason to let
            // everything through; it is a reason to restrict everything.
            _logger.LogError(
                ex, "Could not load the cached USB policy. Treating every storage device as restricted.");
            _grants = UsbGrantSet.Empty;
        }

        _loaded = true;
    }

    private Task<UsbReconcileOutcome> ReconcileLockedAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var devices = SafeEnumerate();

        var restricted = 0;
        var readOnly = 0;
        var failed = 0;

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (device.Class != UsbClass.Storage)
            {
                continue;
            }

            var desired = Desired(device.InstanceId, now);

            UsbEnforcementResult result;
            try
            {
                result = desired == UsbEnforcedState.ReadOnly
                    ? _enforcer.AllowReadOnly(device.InstanceId)
                    : _enforcer.Restrict(device.InstanceId);
            }
            catch (Exception ex)
            {
                result = UsbEnforcementResult.Failed(ex.Message);
            }

            _lastResult[device.InstanceId] = result;

            if (!result.Succeeded)
            {
                failed++;
                _logger.LogError(
                    "Could not apply {Desired} to USB device {InstanceId}: {Error}",
                    desired, device.InstanceId, result.Error);
                continue;
            }

            if (desired == UsbEnforcedState.ReadOnly)
            {
                readOnly++;
            }
            else
            {
                restricted++;
            }
        }

        // Grants for devices that are no longer attached are dropped once they
        // expire; keeping the set trimmed stops the cache growing without bound
        // on a machine that sees many devices.
        PruneExpiredGrants(now);

        return Task.FromResult(new UsbReconcileOutcome(restricted, readOnly, failed));
    }

    /// <summary>
    /// The heart of the security model: read-only if and only if a grant for
    /// this exact device instance is present and has not lapsed.
    /// </summary>
    private UsbEnforcedState Desired(string instanceId, DateTimeOffset now)
    {
        foreach (var grant in _grants.Grants)
        {
            if (string.Equals(grant.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)
                && grant.ExpiresAt > now)
            {
                return UsbEnforcedState.ReadOnly;
            }
        }

        return UsbEnforcedState.Restricted;
    }

    private void PruneExpiredGrants(DateTimeOffset now)
    {
        var live = _grants.Grants.Where(g => g.ExpiresAt > now).ToList();

        if (live.Count != _grants.Grants.Count)
        {
            _logger.LogInformation(
                "{Count} USB grant(s) expired and the affected device(s) are now restricted.",
                _grants.Grants.Count - live.Count);

            _grants = _grants with { Grants = live };
        }
    }

    private IReadOnlyList<UsbDeviceInfo> SafeEnumerate()
    {
        try
        {
            return _enumerator.Enumerate();
        }
        catch (Exception ex)
        {
            // Enumeration failing means we cannot see devices to restrict them.
            // Nothing is opened by this — a device already restricted stays
            // disabled — but it must be loud, not silent.
            _logger.LogError(ex, "USB enumeration failed; no device state could be evaluated this round.");
            return [];
        }
    }
}

/// <param name="Failed">Devices whose desired state could not be applied. Never hidden from the server.</param>
public sealed record UsbReconcileOutcome(int Restricted, int ReadOnly, int Failed)
{
    public int Total => Restricted + ReadOnly + Failed;
}
