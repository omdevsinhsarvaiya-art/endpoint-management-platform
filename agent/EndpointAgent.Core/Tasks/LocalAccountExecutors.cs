using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Shared payload handling for the local-account executors.
/// </summary>
/// <remarks>
/// Every executor here follows the established shape: guard the payload, parse it
/// with <see cref="JsonDocument"/>, call the control abstraction, and return a
/// structured result. Failures inside the control layer are allowed to throw — the
/// dispatcher converts them into a reported failure, so no executor swallows an
/// error and reports success.
/// </remarks>
internal static class LocalAccountPayload
{
    /// <summary>Reads a required string property, or null when absent/blank.</summary>
    public static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool Bool(JsonElement root, string name, bool fallback = false) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}

/// <summary>Base for executors that act on a single local account identified by SID.</summary>
public abstract class LocalAccountTaskExecutorBase(ILocalAccountsControl control, ILogger logger) : ITaskExecutor
{
    protected ILocalAccountsControl Control { get; } = control ?? throw new ArgumentNullException(nameof(control));

    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    public abstract string TaskType { get; }

    public abstract Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default);

    /// <summary>Parses the payload, returning null (and a failure result) when malformed.</summary>
    protected static bool TryParse(AgentTask task, string label, out JsonElement root, out AgentTaskResult? failure)
    {
        root = default;

        if (string.IsNullOrWhiteSpace(task.PayloadJson))
        {
            failure = new AgentTaskResult(false, $"Missing {label} payload.", null);
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(task.PayloadJson);
            root = document.RootElement.Clone();
            failure = null;
            return true;
        }
        catch (JsonException)
        {
            failure = new AgentTaskResult(false, $"Malformed {label} payload.", null);
            return false;
        }
    }

    /// <summary>
    /// Re-checks the last-administrator and protected-account rules against LIVE
    /// Windows state. The server checked the same rules against inventory, which can
    /// be stale; this is the authoritative check immediately before acting.
    /// </summary>
    protected async Task<string?> LiveSafetyRefusalAsync(
        string targetSid, string verb, CancellationToken cancellationToken)
    {
        if (targetSid.EndsWith("-500", StringComparison.OrdinalIgnoreCase))
        {
            return "The built-in Administrator account is protected.";
        }

        var accounts = await Control.GetLiveAccountsAsync(cancellationToken);
        var target = accounts.FirstOrDefault(
            a => string.Equals(a.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        if (target is null || !target.IsAdministrator || !target.Enabled)
        {
            return null;
        }

        var otherEnabledAdmins = accounts.Count(a =>
            a.IsAdministrator && a.Enabled
            && !string.Equals(a.Sid, targetSid, StringComparison.OrdinalIgnoreCase));

        return otherEnabledAdmins == 0
            ? $"Refused: {verb} '{target.Username}' would leave this device with no enabled administrator."
            : null;
    }
}

/// <summary>Creates a local user; the password is redeemed once from the server.</summary>
public sealed class CreateLocalUserExecutor(
    ILocalAccountsControl control,
    ISecretRedeemer secrets,
    ILogger<CreateLocalUserExecutor> logger) : LocalAccountTaskExecutorBase(control, logger)
{
    private readonly ISecretRedeemer _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    public override string TaskType => "CreateLocalUser";

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "create-user", out var root, out var failure))
        {
            return failure!;
        }

        var username = LocalAccountPayload.String(root, "username");
        var secretRef = LocalAccountPayload.String(root, "secretRef");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secretRef))
        {
            return new AgentTaskResult(false, "Create-user payload is incomplete.", null);
        }

        var additionalGroups = new List<string>();
        if (root.TryGetProperty("additionalGroups", out var groups)
            && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind == JsonValueKind.String && group.GetString() is { } name)
                {
                    additionalGroups.Add(name);
                }
            }
        }

        var password = await _secrets.RedeemAsync(secretRef!, cancellationToken);
        if (password is null)
        {
            // One-time reference: expired, already used, or not ours. Never retried
            // blindly - the operator re-issues with a fresh secret.
            return new AgentTaskResult(
                false, "The password reference could not be redeemed; the task was not applied.", null);
        }

        CreatedLocalAccount created;
        try
        {
            created = await Control.CreateUserAsync(
                username!,
                password,
                LocalAccountPayload.String(root, "fullName"),
                LocalAccountPayload.String(root, "description"),
                LocalAccountPayload.Bool(root, "enabled", true),
                LocalAccountPayload.Bool(root, "mustChangePasswordAtNextLogon"),
                LocalAccountPayload.Bool(root, "administrator"),
                additionalGroups,
                cancellationToken);
        }
        finally
        {
            password = null;
        }

        // Report what Windows actually has, not what was asked for. The result carries
        // the SID so the server can correlate it with the next inventory upload, and
        // never carries the password.
        var accountType = created.IsAdministrator ? "Administrator" : "Standard User";

        // A skipped optional group is a successful create the operator still needs to
        // know about, so it is stated in the message rather than left to the result
        // JSON nobody reads.
        var note = created.SkippedGroups.Count == 0
            ? string.Empty
            : $" Not added to {string.Join(", ", created.SkippedGroups.Select(g => $"'{g}'"))}"
              + " — no such group on this device.";

        return new AgentTaskResult(
            true,
            $"Local user '{created.Username}' created as {accountType}.{note}",
            JsonSerializer.Serialize(new
            {
                sid = created.Sid,
                username = created.Username,
                enabled = created.Enabled,
                isAdministrator = created.IsAdministrator,
                isInUsersGroup = created.IsInUsersGroup,
                groups = created.Groups,
                skippedGroups = created.SkippedGroups,
            }));
    }
}

/// <summary>Deletes a local user, after a live last-admin re-check.</summary>
public sealed class DeleteLocalUserExecutor(
    ILocalAccountsControl control, ILogger<DeleteLocalUserExecutor> logger)
    : LocalAccountTaskExecutorBase(control, logger)
{
    public override string TaskType => "DeleteLocalUser";

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "delete-user", out var root, out var failure))
        {
            return failure!;
        }

        var sid = LocalAccountPayload.String(root, "sid");
        if (string.IsNullOrWhiteSpace(sid))
        {
            return new AgentTaskResult(false, "Delete-user payload is incomplete.", null);
        }

        var refusal = await LiveSafetyRefusalAsync(sid!, "deleting", cancellationToken);
        if (refusal is not null)
        {
            return new AgentTaskResult(false, refusal, null);
        }

        await Control.DeleteUserAsync(sid!, cancellationToken);
        return new AgentTaskResult(true, $"Local user '{LocalAccountPayload.String(root, "username") ?? sid}' deleted.", null);
    }
}

/// <summary>Enables or disables a local user (one executor, two task types).</summary>
public abstract class SetLocalUserEnabledExecutorBase(ILocalAccountsControl control, ILogger logger)
    : LocalAccountTaskExecutorBase(control, logger)
{
    protected abstract bool Enable { get; }

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "set-user-enabled", out var root, out var failure))
        {
            return failure!;
        }

        var sid = LocalAccountPayload.String(root, "sid");
        if (string.IsNullOrWhiteSpace(sid))
        {
            return new AgentTaskResult(false, "Set-user-enabled payload is incomplete.", null);
        }

        if (!Enable)
        {
            var refusal = await LiveSafetyRefusalAsync(sid!, "disabling", cancellationToken);
            if (refusal is not null)
            {
                return new AgentTaskResult(false, refusal, null);
            }
        }

        await Control.SetUserEnabledAsync(sid!, Enable, cancellationToken);

        var name = LocalAccountPayload.String(root, "username") ?? sid;
        return new AgentTaskResult(true, $"Local user '{name}' {(Enable ? "enabled" : "disabled")}.", null);
    }
}

public sealed class EnableLocalUserExecutor(ILocalAccountsControl control, ILogger<EnableLocalUserExecutor> logger)
    : SetLocalUserEnabledExecutorBase(control, logger)
{
    public override string TaskType => "EnableLocalUser";
    protected override bool Enable => true;
}

public sealed class DisableLocalUserExecutor(ILocalAccountsControl control, ILogger<DisableLocalUserExecutor> logger)
    : SetLocalUserEnabledExecutorBase(control, logger)
{
    public override string TaskType => "DisableLocalUser";
    protected override bool Enable => false;
}

/// <summary>Resets a local user's password from a one-time secret reference.</summary>
public sealed class ResetLocalUserPasswordExecutor(
    ILocalAccountsControl control,
    ISecretRedeemer secrets,
    ILogger<ResetLocalUserPasswordExecutor> logger) : LocalAccountTaskExecutorBase(control, logger)
{
    private readonly ISecretRedeemer _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    public override string TaskType => "ResetLocalUserPassword";

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "reset-password", out var root, out var failure))
        {
            return failure!;
        }

        var sid = LocalAccountPayload.String(root, "sid");
        var secretRef = LocalAccountPayload.String(root, "secretRef");

        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(secretRef))
        {
            return new AgentTaskResult(false, "Reset-password payload is incomplete.", null);
        }

        var password = await _secrets.RedeemAsync(secretRef!, cancellationToken);
        if (password is null)
        {
            return new AgentTaskResult(false, "The password reference could not be redeemed; the task was not applied.", null);
        }

        try
        {
            await Control.SetPasswordAsync(sid!, password, cancellationToken);
        }
        finally
        {
            password = null;
        }

        var name = LocalAccountPayload.String(root, "username") ?? sid;
        return new AgentTaskResult(true, $"Password reset for local user '{name}'.", null);
    }
}

/// <summary>Requires a password change at next logon.</summary>
public sealed class ForceLocalUserPasswordChangeExecutor(
    ILocalAccountsControl control, ILogger<ForceLocalUserPasswordChangeExecutor> logger)
    : LocalAccountTaskExecutorBase(control, logger)
{
    public override string TaskType => "ForceLocalUserPasswordChange";

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "force-password-change", out var root, out var failure))
        {
            return failure!;
        }

        var sid = LocalAccountPayload.String(root, "sid");
        if (string.IsNullOrWhiteSpace(sid))
        {
            return new AgentTaskResult(false, "Force-password-change payload is incomplete.", null);
        }

        await Control.ForcePasswordChangeAsync(sid!, cancellationToken);

        var name = LocalAccountPayload.String(root, "username") ?? sid;
        return new AgentTaskResult(true, $"Local user '{name}' must change password at next logon.", null);
    }
}

/// <summary>
/// Promotes or demotes a local user by changing real BUILTIN\Administrators
/// membership — the flagship operation of this milestone.
/// </summary>
public sealed class ChangeLocalUserTypeExecutor(
    ILocalAccountsControl control, ILogger<ChangeLocalUserTypeExecutor> logger)
    : LocalAccountTaskExecutorBase(control, logger)
{
    /// <summary>Well-known SID of BUILTIN\Administrators; identical on every Windows install.</summary>
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>Well-known SID of BUILTIN\Users, the baseline every local account belongs to.</summary>
    private const string UsersSid = "S-1-5-32-545";

    public override string TaskType => "ChangeLocalUserType";

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "change-account-type", out var root, out var failure))
        {
            return failure!;
        }

        var sid = LocalAccountPayload.String(root, "sid");
        if (string.IsNullOrWhiteSpace(sid))
        {
            return new AgentTaskResult(false, "Change-account-type payload is incomplete.", null);
        }

        var administrator = LocalAccountPayload.Bool(root, "administrator");

        if (!administrator)
        {
            var refusal = await LiveSafetyRefusalAsync(sid!, "demoting", cancellationToken);
            if (refusal is not null)
            {
                return new AgentTaskResult(false, refusal, null);
            }
        }

        // Demotion removes the account from Administrators. If that were the only
        // change, an account whose ONLY membership was Administrators would come out
        // of this with no local groups at all - the same groupless state that made a
        // created standard user look fine while belonging to nothing. Establish the
        // baseline first, so the account is never briefly in neither group.
        //
        // Adding an existing member succeeds quietly, so this is a no-op for the
        // accounts that already hold the baseline.
        if (!administrator)
        {
            await Control.SetGroupMembershipAsync(UsersSid, sid!, isMember: true, cancellationToken);
        }

        await Control.SetGroupMembershipAsync(AdministratorsSid, sid!, administrator, cancellationToken);

        var name = LocalAccountPayload.String(root, "username") ?? sid;
        var target = administrator ? "Administrator" : "Standard User";
        return new AgentTaskResult(
            true,
            $"Local user '{name}' is now a {target}.",
            JsonSerializer.Serialize(new { accountType = administrator ? "Administrator" : "StandardUser" }));
    }
}

/// <summary>Adds or removes a local group member (one executor, two task types).</summary>
public abstract class LocalGroupMembershipExecutorBase(ILocalAccountsControl control, ILogger logger)
    : LocalAccountTaskExecutorBase(control, logger)
{
    private const string AdministratorsSid = "S-1-5-32-544";

    protected abstract bool Add { get; }

    public override async Task<AgentTaskResult> ExecuteAsync(
        AgentTask task, CancellationToken cancellationToken = default)
    {
        if (!TryParse(task, "group-membership", out var root, out var failure))
        {
            return failure!;
        }

        var groupSid = LocalAccountPayload.String(root, "groupSid");
        var memberSid = LocalAccountPayload.String(root, "memberSid");

        if (string.IsNullOrWhiteSpace(groupSid) || string.IsNullOrWhiteSpace(memberSid))
        {
            return new AgentTaskResult(false, "Group-membership payload is incomplete.", null);
        }

        // Removing someone from Administrators is a demotion, so it earns the same
        // live last-admin protection as an explicit account-type change.
        if (!Add && string.Equals(groupSid, AdministratorsSid, StringComparison.OrdinalIgnoreCase))
        {
            var refusal = await LiveSafetyRefusalAsync(memberSid!, "removing from Administrators", cancellationToken);
            if (refusal is not null)
            {
                return new AgentTaskResult(false, refusal, null);
            }
        }

        await Control.SetGroupMembershipAsync(groupSid!, memberSid!, Add, cancellationToken);

        var groupName = LocalAccountPayload.String(root, "groupName") ?? groupSid;
        var memberName = LocalAccountPayload.String(root, "memberName") ?? memberSid;

        return new AgentTaskResult(
            true,
            Add ? $"'{memberName}' added to '{groupName}'." : $"'{memberName}' removed from '{groupName}'.",
            null);
    }
}

public sealed class AddLocalUserToGroupExecutor(
    ILocalAccountsControl control, ILogger<AddLocalUserToGroupExecutor> logger)
    : LocalGroupMembershipExecutorBase(control, logger)
{
    public override string TaskType => "AddLocalUserToGroup";
    protected override bool Add => true;
}

public sealed class RemoveLocalUserFromGroupExecutor(
    ILocalAccountsControl control, ILogger<RemoveLocalUserFromGroupExecutor> logger)
    : LocalGroupMembershipExecutorBase(control, logger)
{
    public override string TaskType => "RemoveLocalUserFromGroup";
    protected override bool Add => false;
}
