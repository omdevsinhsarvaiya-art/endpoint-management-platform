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

    /// <summary>
    /// Requests per minute per source address allowed on the anonymous enrollment
    /// endpoints (<c>/enroll</c>, <c>/enroll/request</c>, <c>/enroll/claim</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Partitioned by source address, which has to account for NAT: an office rolling
    /// the agent out to fifty machines presents ONE public IP, and each machine asks
    /// once and then polls while it waits for approval. A limit tuned for a single
    /// endpoint would throttle a site against itself during exactly the operation this
    /// platform exists to make easy.
    /// </para>
    /// <para>
    /// The bound is deliberately loose because these endpoints grant nothing on their
    /// own - a request becomes a device only when an authenticated administrator
    /// approves it, and request ids are SHA-256 digests, so enumeration is not a
    /// threat model. What this actually limits is Redis pollution and noise in the
    /// pending list.
    /// </para>
    /// </remarks>
    public int EnrollmentRequestsPerMinutePerAddress { get; init; } = 120;
}
