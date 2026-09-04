namespace EndpointPlatform.Infrastructure.Tests.Agents;

/// <summary>
/// A test that runs against a real, built agent MSI when one is provided.
/// </summary>
/// <remarks>
/// <para>
/// The generated artifacts prove the reader against the format as specified.
/// Only a package WiX actually produced proves it against the format as
/// practised -- string pool code page, mini-stream placement, the Property
/// table as it is really laid out. A 30 MB binary does not belong in the
/// repository, so the path comes from <c>EPP_AGENT_MSI_PATH</c>, and without
/// it the test is reported as SKIPPED: an early <c>return</c> would show green
/// and claim a verification that never ran.
/// </para>
/// </remarks>
public sealed class RealAgentMsiFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "EPP_AGENT_MSI_PATH";

    /// <summary>Agent 1.7.0 as built from bbaaa37 -- the artifact staged for release.</summary>
    public const string Agent170Sha256 = "0d5a33051da1174354937972a78acda1207ba7baf0f67f90f0b22761ae3beda2";

    /// <summary>The configured MSI path, when it names an existing file.</summary>
    public static string? Path
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
        }
    }

    public RealAgentMsiFactAttribute()
    {
        if (Path is null)
        {
            Skip = $"Set {EnvironmentVariable} to a built agent MSI to verify the real artifact; it was not verified in this run.";
        }
    }
}
