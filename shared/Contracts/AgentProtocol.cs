namespace EndpointPlatform.Contracts;

/// <summary>
/// Constants shared by the Agent API and the Windows agent.
/// </summary>
/// <remarks>
/// Both sides compile against this file, so a header name or route prefix cannot
/// drift between them. The wire format itself is documented in
/// <c>docs/agent-protocol.md</c>.
/// </remarks>
public static class AgentProtocol
{
    /// <summary>
    /// Version of the agent wire protocol. The agent sends it on every request and
    /// the server refuses versions it does not understand, so an agent fleet can be
    /// upgraded gradually without the server having to guess what a payload means.
    /// </summary>
    public const int Version = 1;

    /// <summary>Route prefix for every agent-facing endpoint.</summary>
    public const string RoutePrefix = "/agent/v1";

    public static class Headers
    {
        /// <summary>Carries <see cref="Version"/>.</summary>
        public const string ProtocolVersion = "X-Agent-Protocol-Version";

        /// <summary>The enrolled device's identifier.</summary>
        public const string DeviceId = "X-Agent-Device-Id";

        /// <summary>Agent build version, for fleet upgrade visibility.</summary>
        public const string AgentVersion = "X-Agent-Version";
    }

    /// <summary>Agent-facing endpoint paths, relative to <see cref="RoutePrefix"/>.</summary>
    public static class Routes
    {
        public const string Enroll = "/enroll";
        public const string Heartbeat = "/heartbeat";
        public const string Inventory = "/inventory";
    }
}
