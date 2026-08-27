using System.Text.Json;
using EndpointAgent.Core.Identity;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Applies the complete set of administrator elevations the server has authorized
/// for this machine.
/// </summary>
/// <remarks>
/// <para>
/// Whole state, so this never merges: an account absent from the payload is not
/// authorized, and its rights are withdrawn. That makes revocation the absence of
/// an entry rather than a second message that could go missing.
/// </para>
/// <para>
/// The parse is defensive in one direction only. A payload that fails to parse is
/// rejected outright and the previous set stays in force — a corrupted message is
/// not evidence that an administrator revoked anything, and treating it as an
/// empty set would let a garbled task strip a legitimate elevation. An individual
/// entry that is malformed or already expired is dropped instead, so the only
/// entries that can widen access are well-formed and in date.
/// </para>
/// </remarks>
public sealed class ApplyLocalAdminElevationExecutor(
    LocalAdminElevationManager manager,
    TimeProvider timeProvider,
    ILogger<ApplyLocalAdminElevationExecutor> logger) : ITaskExecutor
{
    /// <summary>Refuses absurd payloads before parsing them.</summary>
    public const int MaxElevations = 64;

    private readonly LocalAdminElevationManager _manager = manager
        ?? throw new ArgumentNullException(nameof(manager));

    /// <summary>
    /// The injected clock, never <c>DateTimeOffset.UtcNow</c>.
    /// </summary>
    /// <remarks>
    /// Every expiry decision in this feature runs off an injected clock so it can
    /// be driven deterministically in tests. Reading the wall clock here is the
    /// defect that shipped once in the USB executor: it passed locally in the
    /// morning and failed in CI after the fixture's timestamp had gone by.
    /// </remarks>
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<ApplyLocalAdminElevationExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string TaskType => "ApplyLocalAdminElevation";

    public async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var grants, out var issuedAt, out var parseError))
        {
            return new AgentTaskResult(false, parseError, null);
        }

        var outcome = await _manager.ApplyAsync(grants, issuedAt, cancellationToken);

        // Structured evidence, naming the accounts and what actually happened to
        // each. The console needs to distinguish "authorized and applied" from
        // "authorized but refused", and a prose message cannot carry that.
        var evidence = JsonSerializer.Serialize(new
        {
            elevated = outcome.Elevated,
            lowered = outcome.Lowered,
            refused = outcome.Refused.Select(r => new { sid = r.Sid, reason = r.Reason }).ToList(),
        });

        if (!outcome.Succeeded)
        {
            // Reported as a failure. Saying "applied" while an account is still
            // elevated past its window would put a green tick beside a control
            // that is not in place.
            var first = outcome.Refused[0];

            return new AgentTaskResult(
                false,
                $"{outcome.Refused.Count} account(s) could not be reconciled. First: {first.Reason}",
                evidence);
        }

        _logger.LogInformation(
            "Elevation policy applied: {Elevated} elevated, {Lowered} lowered.",
            outcome.Elevated.Count, outcome.Lowered.Count);

        return new AgentTaskResult(
            true,
            outcome.Elevated.Count == 0 && outcome.Lowered.Count == 0
                ? "Elevation policy applied. No account needed changing."
                : $"Elevation policy applied: {outcome.Elevated.Count} elevated, "
                    + $"{outcome.Lowered.Count} returned to standard.",
            evidence);
    }

    private bool TryParse(
        string? payloadJson,
        out List<ElevationGrant> grants,
        out DateTimeOffset issuedAt,
        out string? error)
    {
        grants = [];
        issuedAt = default;
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Malformed elevation payload: it was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            issuedAt = root.GetProperty("issuedAt").GetDateTimeOffset();

            var list = root.GetProperty("elevations");
            if (list.ValueKind != JsonValueKind.Array)
            {
                error = "Malformed elevation payload: 'elevations' was not an array.";
                return false;
            }

            if (list.GetArrayLength() > MaxElevations)
            {
                error = $"Refused an elevation set naming more than {MaxElevations} accounts.";
                return false;
            }

            var now = _timeProvider.GetUtcNow();

            foreach (var element in list.EnumerateArray())
            {
                if (!TryParseGrant(element, now, out var grant, out var reason))
                {
                    // Dropped, not fatal: a bad entry must not void the good ones,
                    // and dropping it can only narrow access.
                    _logger.LogWarning("Ignoring an elevation entry: {Reason}", reason);
                    continue;
                }

                grants.Add(grant);
            }

            return true;
        }
        catch (Exception ex)
            when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = "Malformed elevation payload.";
            return false;
        }
    }

    private static bool TryParseGrant(
        JsonElement element, DateTimeOffset now, out ElevationGrant grant, out string reason)
    {
        grant = null!;
        reason = "";

        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "it was not an object";
            return false;
        }

        // Every field's JSON kind is checked before it is read. The typed
        // accessors throw on a mismatch, and an exception escaping here would be
        // caught by the caller as "the whole payload is malformed" -- so one
        // wrong-typed field in one entry would discard every other elevation in
        // the message.
        if (!element.TryGetProperty("sid", out var sidElement)
            || sidElement.ValueKind != JsonValueKind.String
            || sidElement.GetString() is not { Length: > 0 } sid)
        {
            reason = "it named no account";
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

        grant = new ElevationGrant(sid, expiresAt);
        return true;
    }
}
