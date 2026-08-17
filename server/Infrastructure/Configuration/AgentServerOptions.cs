using System.ComponentModel.DataAnnotations;

namespace EndpointPlatform.Infrastructure.Configuration;

/// <summary>Server-side policy for the agent fleet.</summary>
public sealed class AgentServerOptions
{
    public const string SectionName = "AgentServer";

    /// <summary>Interval the server asks agents to heartbeat at (returned in every heartbeat response).</summary>
    [Range(15, 3600)]
    public int HeartbeatIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Seconds without a heartbeat after which a device counts as offline.
    /// Kept at a multiple of the heartbeat interval so one dropped packet does not
    /// flap the fleet's status.
    /// </summary>
    [Range(30, 86_400)]
    public int OfflineAfterSeconds { get; init; } = 180;
}
