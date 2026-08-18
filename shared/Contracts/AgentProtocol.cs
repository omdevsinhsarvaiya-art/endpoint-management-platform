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

        /// <summary>
        /// Device credential, presented as <c>keyId.secret</c>. Sent only over TLS.
        /// A dedicated header rather than <c>Authorization: Bearer</c> so that agent
        /// credentials can never be confused with (or replayed as) administrator
        /// bearer tokens by any intermediary or log pipeline.
        /// </summary>
        public const string Credential = "X-Agent-Credential";
    }

    /// <summary>Agent-facing endpoint paths, relative to <see cref="RoutePrefix"/>.</summary>
    public static class Routes
    {
        public const string Enroll = "/enroll";
        public const string Heartbeat = "/heartbeat";
        public const string Inventory = "/inventory";

        /// <summary>Agent claims queued tasks (GET) here.</summary>
        public const string Tasks = "/tasks";

        /// <summary>Agent posts a task result to {RoutePrefix}/tasks/{id}/result.</summary>
        public const string TaskResultSuffix = "/result";

        /// <summary>Agent pulls its effective policies (GET) here.</summary>
        public const string Policies = "/policies";

        /// <summary>Agent posts compliance to {RoutePrefix}/policies/compliance.</summary>
        public const string PolicyComplianceSuffix = "/compliance";
    }
}
