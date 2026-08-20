namespace EndpointAgent.Core;

/// <summary>
/// Where the agent keeps state on disk.
/// </summary>
/// <remarks>
/// <para>
/// One definition, shared by the service host, the credential store and the
/// installer. These paths were previously computed inline in more than one place,
/// which is the kind of duplication that silently splits an installed agent's
/// state from the state its own components look for.
/// </para>
/// <para>
/// State lives under ProgramData, never under Program Files. The service account
/// must be able to write its credential and logs, but must NOT be able to rewrite
/// the directory its own binaries load from — otherwise a compromise of the agent
/// process becomes a persistent compromise of the agent itself.
/// </para>
/// </remarks>
public static class AgentPaths
{
    /// <summary>Folder name used under ProgramData.</summary>
    public const string FolderName = "EndpointPlatformAgent";

    /// <summary>
    /// <c>C:\ProgramData\EndpointPlatformAgent</c>. Holds the device credential,
    /// machine-wide configuration and logs. The installer restricts its ACL to
    /// SYSTEM and Administrators.
    /// </summary>
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        FolderName);

    /// <summary>Machine-wide configuration written by the installer.</summary>
    public static string ConfigFile { get; } = Path.Combine(StateDirectory, "agent.config.json");

    /// <summary><c>…\Logs</c>. Rolling operational log files.</summary>
    public static string LogDirectory { get; } = Path.Combine(StateDirectory, "Logs");
}
