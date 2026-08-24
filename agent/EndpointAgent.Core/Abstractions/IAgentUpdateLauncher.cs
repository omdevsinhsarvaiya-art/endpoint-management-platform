namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The platform-specific half of agent self-update: signature verification and
/// handing a fully-verified MSI to the operating system for installation in a
/// way that survives this very process being stopped.
/// </summary>
/// <remarks>
/// Self-update cannot reuse <see cref="IPackageInstaller"/>: that path installs
/// in-process, and the agent's own MSI upgrade stops the agent service — the
/// process running the install — mid-install. The launcher therefore schedules
/// the installation with the OS (on Windows, a one-shot Task Scheduler entry
/// running <c>msiexec</c> as SYSTEM) and returns; the installer then stops this
/// service, replaces it, and starts the new one. The agent never launches a
/// process itself, and nothing here accepts a command — only the path of a file
/// this agent has already hash- and signature-verified.
/// </remarks>
public interface IAgentUpdateLauncher
{
    /// <summary>
    /// Verifies the file's Authenticode signature and pins the signer subject.
    /// Returns null when acceptable, otherwise the refusal reason. A null
    /// <paramref name="requiredSignerSubject"/> means the release was published
    /// unsigned on purpose and the signature gate is skipped by policy — the
    /// hash gate is never skipped.
    /// </summary>
    ValueTask<string?> VerifySignatureAsync(
        string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules installation of the verified MSI to begin shortly after this
    /// call returns. Never blocks on the install itself.
    /// </summary>
    ValueTask ScheduleInstallAsync(
        string msiPath, string installLogPath, CancellationToken cancellationToken = default);
}
