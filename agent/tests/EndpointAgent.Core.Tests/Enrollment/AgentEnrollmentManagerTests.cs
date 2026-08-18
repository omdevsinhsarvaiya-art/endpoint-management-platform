using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Enrollment;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Enrollment;

public sealed class AgentEnrollmentManagerTests
{
    private readonly FakeCredentialStore _store = new();
    private readonly FakeApiClient _api = new();
    private readonly FakeSystemInfo _systemInfo = new();

    private AgentEnrollmentManager CreateManager() =>
        new(_api, _store, _systemInfo, NullLogger<AgentEnrollmentManager>.Instance);

    [Fact]
    public async Task An_existing_credential_is_returned_without_contacting_the_server()
    {
        var existing = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));
        _store.Stored = existing;

        var result = await CreateManager().EnsureEnrolledAsync("some-token", "1.0.0");

        result.ShouldBe(existing);
        _api.EnrollCalls.ShouldBe(0, "a machine with an identity must not re-enroll on every start");
    }

    [Fact]
    public async Task Without_credential_or_token_the_agent_does_not_enroll()
    {
        var result = await CreateManager().EnsureEnrolledAsync(enrollmentToken: null, "1.0.0");

        result.ShouldBeNull();
        _api.EnrollCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Successful_enrollment_persists_the_credential_before_returning()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Success(new EnrollResponse(
            Guid.CreateVersion7(), new string('c', 32), new string('d', 64), ReEnrolled: false));

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldNotBeNull();
        _store.Stored.ShouldBe(result, "the credential must be persisted so the identity survives a crash");
        _api.LastEnrollRequest.ShouldNotBeNull();
        _api.LastEnrollRequest.EnrollmentToken.ShouldBe("valid-token");
        _api.LastEnrollRequest.Hostname.ShouldBe(_systemInfo.HostName);
    }

    [Fact]
    public async Task A_rejected_enrollment_returns_null_and_stores_nothing()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Rejected();

        var result = await CreateManager().EnsureEnrolledAsync("bad-token", "1.0.0");

        result.ShouldBeNull();
        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task A_transient_failure_returns_null_so_the_caller_can_back_off_and_retry()
    {
        _api.EnrollResult = AgentApiResult<EnrollResponse>.Transient();

        var result = await CreateManager().EnsureEnrolledAsync("valid-token", "1.0.0");

        result.ShouldBeNull();
        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task Discarding_a_rejected_credential_clears_the_store()
    {
        _store.Stored = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), new string('b', 64));

        await CreateManager().DiscardRejectedCredentialAsync();

        _store.Stored.ShouldBeNull();
    }

    [Fact]
    public void The_credential_never_prints_its_secret()
    {
        var credential = new DeviceCredential(Guid.CreateVersion7(), new string('a', 32), "SUPER-SECRET-VALUE");

        credential.ToString().ShouldNotContain("SUPER-SECRET-VALUE");
        credential.ToString().ShouldContain("<redacted>");
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeCredentialStore : IDeviceCredentialStore
    {
        public DeviceCredential? Stored { get; set; }

        public ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Stored);

        public ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
        {
            Stored = credential;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Stored is not null);

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            Stored = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApiClient : IAgentApiClient
    {
        public int EnrollCalls { get; private set; }

        public EnrollRequest? LastEnrollRequest { get; private set; }

        public AgentApiResult<EnrollResponse> EnrollResult { get; set; } =
            AgentApiResult<EnrollResponse>.Transient();

        public Task<AgentApiResult<EnrollResponse>> EnrollAsync(
            EnrollRequest request, CancellationToken cancellationToken = default)
        {
            EnrollCalls++;
            LastEnrollRequest = request;
            return Task.FromResult(EnrollResult);
        }

        public Task<AgentApiResult<HeartbeatResponse>> HeartbeatAsync(
            HeartbeatRequest request, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<HeartbeatResponse>.Transient());

        public Task<AgentApiResult<InventoryResponse>> UploadInventoryAsync(
            InventoryReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<InventoryResponse>.Transient());

        public Task<AgentApiResult<AgentTaskListResponse>> ClaimTasksAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<AgentTaskListResponse>.Success(new AgentTaskListResponse([])));

        public Task<AgentApiResult<Unit>> PostTaskResultAsync(
            Guid taskId, AgentTaskResult result, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));

        public Task<AgentApiResult<AgentPolicyListResponse>> GetPoliciesAsync(
            DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<AgentPolicyListResponse>.Success(new AgentPolicyListResponse([])));

        public Task<AgentApiResult<Unit>> PostComplianceAsync(
            AgentPolicyComplianceReport report, DeviceCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentApiResult<Unit>.Success(Unit.Value));
    }

    private sealed class FakeSystemInfo : ISystemInfoProvider
    {
        public string HostName => "FAKE-PC";

        public string GetHostName() => HostName;

        public ValueTask<string> GetOperatingSystemDescriptionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("Fake OS 1.0");

        public ValueTask<string> GetMachineIdentifierAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("fake-machine-id");
    }
}
