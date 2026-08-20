using System.Net;
using System.Net.Http.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Communication;

/// <summary>
/// HTTP implementation of <see cref="IAgentApiClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The credential is attached per request and never stored on the
/// <see cref="HttpClient"/>'s default headers, so a client instance can never
/// leak a credential into a request that should not carry one (enrollment).
/// </para>
/// <para>
/// Log discipline: URLs and status codes are logged; header values, bodies and
/// credential material never are.
/// </para>
/// </remarks>
public sealed class AgentApiClient(HttpClient httpClient, ILogger<AgentApiClient> logger) : IAgentApiClient
{
    private readonly HttpClient _httpClient = httpClient
        ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly ILogger<AgentApiClient> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task<AgentApiResult<EnrollResponse>> EnrollAsync(
        EnrollRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Enroll)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);

        return await SendAsync<EnrollResponse>(message, "enroll", cancellationToken);
    }

    public async Task<AgentApiResult<EnrollmentRequestResponse>> RequestEnrollmentAsync(
        EnrollmentRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollRequest)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);

        return await SendAsync<EnrollmentRequestResponse>(message, "enroll-request", cancellationToken);
    }

    public async Task<AgentApiResult<EnrollmentClaimResponse>> ClaimEnrollmentAsync(
        EnrollmentClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.EnrollClaim)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);

        // The request secret is in this body. SendAsync must never log request
        // content, which it does not - it logs status codes and the operation name.
        return await SendAsync<EnrollmentClaimResponse>(message, "enroll-claim", cancellationToken);
    }

    public async Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
        HeartbeatRequest request,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Heartbeat)
        {
            Content = JsonContent.Create(request),
        };

        AddProtocolHeaders(message, request.AgentVersion);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<HeartbeatResponse>(message, "heartbeat", cancellationToken);
    }

    public async Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
        InventoryReport report,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Inventory)
        {
            Content = JsonContent.Create(report),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<InventoryResponse>(message, "inventory", cancellationToken);
    }

    public async Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Tasks);

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<AgentTaskListResponse>(message, "claim-tasks", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> PostTaskResultAsync(
        Guid taskId,
        AgentTaskResult result,
        DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AgentProtocol.RoutePrefix}{AgentProtocol.Routes.Tasks}/{taskId}{AgentProtocol.Routes.TaskResultSuffix}")
        {
            Content = JsonContent.Create(result),
        };

        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendNoContentAsync(message, "task-result", cancellationToken);
    }

    public async Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
        DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var message = new HttpRequestMessage(HttpMethod.Get, AgentProtocol.RoutePrefix + AgentProtocol.Routes.Policies);
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());
        return await SendAsync<AgentPolicyListResponse>(message, "get-policies", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> PostComplianceAsync(
        AgentPolicyComplianceReport report, DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(credential);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix)
        {
            Content = JsonContent.Create(report),
        };
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());
        return await SendNoContentAsync(message, "post-compliance", cancellationToken);
    }

    public async Task<AgentApiResult<Unit>> DownloadPackageAsync(
        Guid packageId, Stream destination, DeviceCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            AgentProtocol.RoutePrefix + AgentProtocol.Routes.Packages + "/" + packageId + AgentProtocol.Routes.PackageContentSuffix);
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent package download failed to reach the server: {Reason}", ex.GetType().Name);
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return AgentApiResult<Unit>.Rejected();
            }

            if (!response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Transient();
            }

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await source.CopyToAsync(destination, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.LogWarning("Agent package download stream failed: {Reason}", ex.GetType().Name);
                return AgentApiResult<Unit>.Transient();
            }

            return AgentApiResult<Unit>.Success(Unit.Value);
        }
    }

    public async Task<AgentApiResult<RedeemSecretResponse>> RedeemSecretAsync(
        string secretReference, DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        ArgumentNullException.ThrowIfNull(credential);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, AgentProtocol.RoutePrefix + AgentProtocol.Routes.SecretRedeem)
        {
            Content = JsonContent.Create(new RedeemSecretRequest(secretReference)),
        };
        AddProtocolHeaders(message, Core.AgentVersion.Current);
        message.Headers.Add(AgentProtocol.Headers.Credential, credential.ToHeaderValue());
        message.Headers.Add(AgentProtocol.Headers.DeviceId, credential.DeviceId.ToString());

        return await SendAsync<RedeemSecretResponse>(message, "redeem-secret", cancellationToken);
    }

    private static void AddProtocolHeaders(HttpRequestMessage message, string agentVersion)
    {
        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());
        message.Headers.Add(AgentProtocol.Headers.AgentVersion, agentVersion);
    }

    private async Task<AgentApiResult<T>> SendAsync<T>(
        HttpRequestMessage message,
        string operation,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Shutdown, not a failure.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent {Operation} request failed to reach the server: {Reason}",
                operation, ex.GetType().Name);
            return AgentApiResult<T>.Transient();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

                if (body is null)
                {
                    _logger.LogWarning("Agent {Operation} returned success with an empty body.", operation);
                    return AgentApiResult<T>.Transient();
                }

                return AgentApiResult<T>.Success(body);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Agent {Operation} was refused: credential not accepted (401).", operation);
                return AgentApiResult<T>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogWarning(
                    "Agent {Operation} was rejected by the server: HTTP {StatusCode}.",
                    operation,
                    (int)response.StatusCode);
                return AgentApiResult<T>.Rejected();
            }

            _logger.LogWarning(
                "Agent {Operation} failed with server error HTTP {StatusCode}; will retry.",
                operation,
                (int)response.StatusCode);
            return AgentApiResult<T>.Transient();
        }
    }

    private async Task<AgentApiResult<Unit>> SendNoContentAsync(
        HttpRequestMessage message,
        string operation,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Agent {Operation} request failed to reach the server: {Reason}",
                operation, ex.GetType().Name);
            return AgentApiResult<Unit>.Transient();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return AgentApiResult<Unit>.Success(Unit.Value);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return AgentApiResult<Unit>.Unauthorized();
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogWarning("Agent {Operation} rejected: HTTP {Status}.", operation, (int)response.StatusCode);
                return AgentApiResult<Unit>.Rejected();
            }

            return AgentApiResult<Unit>.Transient();
        }
    }

}
