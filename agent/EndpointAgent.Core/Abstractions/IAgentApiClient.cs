using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The agent's view of the server. Abstracted so the enrollment/heartbeat logic
/// is unit-testable without a network.
/// </summary>
public interface IAgentApiClient
{
    /// <summary>
    /// Exchanges an enrollment token for a device credential. The token is used
    /// once and not retained by the client.
    /// </summary>
    Task<AgentApiResult<EnrollResponse>> EnrollAsync(
        EnrollRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an authenticated heartbeat.</summary>
    Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
        HeartbeatRequest request,
        DeviceCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of one API call, flattened to what retry logic needs: success with a
/// body, an authoritative rejection (do not retry with the same input), or a
/// transient failure (retry with backoff).
/// </summary>
public sealed record AgentApiResult<T>(T? Value, AgentApiStatus Status)
{
    public bool IsSuccess => Status == AgentApiStatus.Success;

    public static AgentApiResult<T> Success(T value) => new(value, AgentApiStatus.Success);

    public static AgentApiResult<T> Rejected() => new(default, AgentApiStatus.Rejected);

    public static AgentApiResult<T> Unauthorized() => new(default, AgentApiStatus.Unauthorized);

    public static AgentApiResult<T> Transient() => new(default, AgentApiStatus.TransientFailure);
}

public enum AgentApiStatus
{
    Success = 0,

    /// <summary>4xx other than 401: the server understood and said no. Retrying the same request is pointless.</summary>
    Rejected = 1,

    /// <summary>401: the credential is not accepted. Requires re-enrollment, not retry.</summary>
    Unauthorized = 2,

    /// <summary>Network failure, timeout or 5xx: retry with backoff.</summary>
    TransientFailure = 3,
}
