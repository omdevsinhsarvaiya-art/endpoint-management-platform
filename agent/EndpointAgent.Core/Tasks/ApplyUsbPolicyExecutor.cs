using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Usb;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Applies the complete USB storage policy the server has issued for this
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// The payload is whole state, so this executor never merges: it replaces. A
/// device absent from the grant list is restricted, which makes revocation the
/// absence of an entry rather than a second message that could go missing.
/// </para>
/// <para>
/// Every parse failure lands on the safe side. A malformed payload is rejected
/// outright and the previous policy stays in force — it is <em>not</em> treated
/// as an empty grant list, because "the payload was garbage" is not evidence
/// that an administrator revoked anything, and silently restricting on a bad
/// parse would let a corrupted message cancel a legitimate grant. Conversely a
/// grant entry that is individually malformed, or that has already expired, is
/// dropped rather than guessed at, so the only entries that can widen access are
/// well-formed and in date.
/// </para>
/// </remarks>
public sealed class ApplyUsbPolicyExecutor(
    UsbPolicyManager policyManager,
    TimeProvider timeProvider,
    ILogger<ApplyUsbPolicyExecutor> logger) : ITaskExecutor
{
    /// <summary>Refuses absurd grant counts before parsing them.</summary>
    public const int MaxGrants = 64;

    private readonly UsbPolicyManager _policyManager = policyManager
        ?? throw new ArgumentNullException(nameof(policyManager));

    /// <summary>
    /// The injected clock, not <c>DateTimeOffset.UtcNow</c>.
    /// </summary>
    /// <remarks>
    /// Every expiry decision in this feature is made against an injected clock so
    /// it can be driven deterministically in tests. Reading the wall clock here
    /// made the executor's behaviour depend on the actual time of day — which is
    /// how it shipped once, passing locally in the morning and failing in CI
    /// after the fixture's expiry timestamp had passed.
    /// </remarks>
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<ApplyUsbPolicyExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string TaskType => "ApplyUsbPolicy";

    public async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var grants, out var issuedAt, out var parseError))
        {
            return new AgentTaskResult(false, parseError, null);
        }

        var outcome = await _policyManager.ApplyPolicyAsync(grants, issuedAt, cancellationToken);

        _logger.LogInformation(
            "USB policy applied: {ReadOnly} read-only, {Restricted} restricted, {Failed} failed.",
            outcome.ReadOnly, outcome.Restricted, outcome.Failed);

        // A partial failure is reported as a failure. The alternative — saying
        // "applied" when a device could not actually be restricted — would put a
        // green tick next to a control that is not in place.
        if (outcome.Failed > 0)
        {
            return new AgentTaskResult(
                false,
                $"USB policy applied to {outcome.Restricted + outcome.ReadOnly} device(s), but "
                + $"{outcome.Failed} could not be enforced. Those devices are reported unenforced.",
                null);
        }

        return new AgentTaskResult(
            true,
            outcome.Total == 0
                ? "USB policy applied. No USB storage is currently attached."
                : $"USB policy applied: {outcome.ReadOnly} read-only, {outcome.Restricted} restricted.",
            null);
    }

    private bool TryParse(
        string? payloadJson,
        out List<UsbGrantRecord> grants,
        out DateTimeOffset issuedAt,
        out string? error)
    {
        grants = [];
        issuedAt = default;
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Malformed USB policy payload: it was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            issuedAt = root.GetProperty("issuedAt").GetDateTimeOffset();

            var grantsElement = root.GetProperty("grants");
            if (grantsElement.ValueKind != JsonValueKind.Array)
            {
                error = "Malformed USB policy payload: 'grants' was not an array.";
                return false;
            }

            if (grantsElement.GetArrayLength() > MaxGrants)
            {
                error = $"Refused a USB policy naming more than {MaxGrants} devices.";
                return false;
            }

            var now = _timeProvider.GetUtcNow();

            foreach (var element in grantsElement.EnumerateArray())
            {
                if (!TryParseGrant(element, now, out var grant, out var reason))
                {
                    // Dropped, not fatal: a bad entry must not be able to void the
                    // good ones, and dropping it can only narrow access.
                    _logger.LogWarning("Ignoring a USB grant entry: {Reason}", reason);
                    continue;
                }

                grants.Add(grant);
            }

            return true;
        }
        catch (Exception ex)
            when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = "Malformed USB policy payload.";
            return false;
        }
    }

    private static bool TryParseGrant(
        JsonElement element, DateTimeOffset now, out UsbGrantRecord grant, out string reason)
    {
        grant = null!;
        reason = "";

        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "it was not an object";
            return false;
        }

        // Every field is checked for its JSON kind before it is read. The typed
        // accessors throw on a mismatch, and an exception escaping here would be
        // caught by the caller as "the whole payload is malformed" — so one
        // wrong-typed field in one entry would discard every other grant in the
        // message. Checking the kind keeps a bad entry's blast radius to itself.
        if (!element.TryGetProperty("instanceId", out var instanceElement)
            || instanceElement.ValueKind != JsonValueKind.String
            || instanceElement.GetString() is not { Length: > 0 } instanceId)
        {
            reason = "it named no device instance";
            return false;
        }

        // The only accepted value. An unrecognised policy — including any future
        // attempt to express write access, and including the enum-as-number
        // serialisation that has bitten this codebase before — is refused rather
        // than approximated, so this agent cannot be talked into a state it does
        // not implement.
        if (!element.TryGetProperty("policy", out var policyElement)
            || policyElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                policyElement.GetString(), nameof(UsbEnforcedState.ReadOnly), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"its policy was not the string '{nameof(UsbEnforcedState.ReadOnly)}'";
            return false;
        }

        if (!element.TryGetProperty("expiresAt", out var expiryElement)
            || expiryElement.ValueKind != JsonValueKind.String
            || !expiryElement.TryGetDateTimeOffset(out var expiresAt))
        {
            reason = "it carried no usable expiry";
            return false;
        }

        if (expiresAt <= now)
        {
            reason = $"it had already expired at {expiresAt:O}";
            return false;
        }

        grant = new UsbGrantRecord(instanceId, expiresAt);
        return true;
    }
}
