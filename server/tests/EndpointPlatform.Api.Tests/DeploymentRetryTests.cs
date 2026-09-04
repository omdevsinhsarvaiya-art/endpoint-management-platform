using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Deployment reliability: retrying what failed, cancelling what has not started,
/// and refusing to do either where it would be wrong.
/// </summary>
/// <remarks>
/// A retry is a fresh decision about the world as it is now, not a replay of an
/// old one. Most of these tests are therefore about what a retry declines to do:
/// it must not reinstall a device that has since become compliant, must not
/// downgrade one that moved ahead, and must not touch a device that has been
/// retired or a package that has been withdrawn since the original run.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeploymentRetryTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri Deployments = new("/admin/v1/deployments", UriKind.Relative);
    private const string ProductCode = "{5D1F0C33-1111-4444-8888-AAAABBBBCCCC}";

    private sealed record CreateResponse(Guid DeploymentId, int Targeted, int Queued, int Skipped);

    private sealed record CancelResponse(Guid DeploymentId, int Considered, int Cancelled);

    private sealed record Tally(
        int Total, int Pending, int Installing, int Succeeded, int Failed, int Expired, int Skipped,
        int Offline, int Cancelled);

    private sealed record DeviceResult(
        Guid DeviceId, string Hostname, string? DisplayName, string DeviceStatus, DateTimeOffset? LastSeenAt,
        string Status, string Reason, string? ObservedVersion, Guid? TaskId, string? ResultMessage,
        DateTimeOffset? CompletedAt, int Attempt);

    private sealed record Detail(
        Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
        string CreatedByDisplay, DateTimeOffset CreatedAt, Tally Tally, IReadOnlyList<DeviceResult> Targets);

    private async Task<HttpClient> AdminAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    private async Task<Guid> RegisterPackageAsync(HttpClient client, string version)
    {
        var bytes = Encoding.UTF8.GetBytes("msi-" + Guid.CreateVersion7().ToString("N") + new string('z', 300));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var form = new MultipartFormDataContent
        {
            { file, "file", "app.msi" },
            { new StringContent("Retry App"), "name" },
            { new StringContent(version), "version" },
            { new StringContent("Contoso"), "publisher" },
            { new StringContent(sha), "sha256" },
            { new StringContent(ProductCode), "msiProductCode" },
        };

        (await client.PostAsync(new Uri("/admin/v1/packages/", UriKind.Relative), form))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var db = _fixture.CreateDbContext();
        return await db.SoftwarePackages.Where(p => p.Sha256 == sha).Select(p => p.Id).SingleAsync();
    }

    private async Task<Guid> SeedDeviceAsync(
        string hostname, DeviceStatus status = DeviceStatus.Active, DateTimeOffset? lastSeen = null)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"retry-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "retry-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", "1.5.0",
            "Microsoft Windows 11 Pro", token.Id, now);

        if (status == DeviceStatus.Retired)
        {
            device.Retire();
        }

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        if (lastSeen is { } seen)
        {
            // Written directly: the domain only advances last-seen through a real
            // heartbeat, and these tests need a device that has been silent.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE endpoint_platform.devices SET last_seen_at = {0} WHERE id = {1}", seen, device.Id);
        }

        return device.Id;
    }

    private async Task<Guid> DeployAsync(HttpClient client, Guid packageId, params Guid[] deviceIds)
    {
        var response = await client.PostAsJsonAsync(Deployments, new
        {
            packageId,
            deviceIds,
            groupIds = Array.Empty<Guid>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<CreateResponse>())!.DeploymentId;
    }

    /// <summary>Drives a queued task to a terminal state, as the agent would.</summary>
    private async Task CompleteTaskAsync(Guid deviceId, bool succeeded, string message)
    {
        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallPackage)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();

        var now = DateTimeOffset.UtcNow;
        task.TryDeliver(now);
        task.TryComplete(succeeded, message, null, now);
        await db.SaveChangesAsync();
    }

    private async Task<Detail> DetailAsync(HttpClient client, Guid deploymentId) =>
        (await client.GetFromJsonAsync<Detail>(
            new Uri($"/admin/v1/deployments/{deploymentId}", UriKind.Relative)))!;

    private static async Task<int> TaskCountAsync(AdminApiPostgresFixture f, Guid deviceId)
    {
        await using var db = f.CreateDbContext();
        return await db.DeviceTasks.CountAsync(
            t => t.DeviceId == deviceId && t.Type == DeviceTaskType.InstallPackage);
    }

    private async Task<CreateResponse> RetryAsync(HttpClient client, Guid deploymentId)
    {
        var response = await client.PostAsync(
            new Uri($"/admin/v1/deployments/{deploymentId}/retry", UriKind.Relative), null);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<CreateResponse>())!;
    }

    // ------------------------------------------------------------------ retry

    /// <summary>Only the failed device is retried, and history is kept.</summary>
    [Fact]
    public async Task Retry_reruns_the_failed_target_and_leaves_the_successful_one_alone()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var good = await SeedDeviceAsync("RTY-GOOD");
        var bad = await SeedDeviceAsync("RTY-BAD");

        var deploymentId = await DeployAsync(client, packageId, good, bad);
        await CompleteTaskAsync(good, succeeded: true, "Installed.");
        await CompleteTaskAsync(bad, succeeded: false, "MSI installation failed (exit code 1603).");

        var result = await RetryAsync(client, deploymentId);

        result.Targeted.ShouldBe(1);
        result.Queued.ShouldBe(1);

        (await TaskCountAsync(_fixture, good)).ShouldBe(1, "a successful device must not be reinstalled");
        (await TaskCountAsync(_fixture, bad)).ShouldBe(2);

        var detail = await DetailAsync(client, deploymentId);

        // Attempt 1 survives intact alongside attempt 2.
        detail.Targets.Count(t => t.DeviceId == bad).ShouldBe(2);
        detail.Targets.Where(t => t.DeviceId == bad).Select(t => t.Attempt)
            .OrderBy(a => a).ShouldBe([1, 2]);
        detail.Targets.Single(t => t.DeviceId == bad && t.Attempt == 1).Status.ShouldBe("Failed");
        detail.Targets.Single(t => t.DeviceId == good).Attempt.ShouldBe(1);
    }

    /// <summary>
    /// A device that acquired the software by other means between the failure and
    /// the retry needs nothing, and gets nothing.
    /// </summary>
    [Fact]
    public async Task Retry_skips_a_target_that_has_since_become_compliant()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-NOWOK");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: false, "Could not download the package (transient).");

        await using (var db = _fixture.CreateDbContext())
        {
            db.DeviceSoftware.Add(new DeviceSoftware(
                device, "Retry App", "3.0.0", "Contoso", null, null, "x64", DateTimeOffset.UtcNow,
                "Machine", null, ProductCode));
            await db.SaveChangesAsync();
        }

        var result = await RetryAsync(client, deploymentId);

        result.Targeted.ShouldBe(1);
        result.Queued.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        (await TaskCountAsync(_fixture, device)).ShouldBe(1);
    }

    /// <summary>A retry must not become a downgrade.</summary>
    [Fact]
    public async Task Retry_does_not_downgrade_a_target_that_moved_ahead()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-AHEAD");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: false, "MSI installation failed.");

        await using (var db = _fixture.CreateDbContext())
        {
            db.DeviceSoftware.Add(new DeviceSoftware(
                device, "Retry App", "4.1.0", "Contoso", null, null, "x64", DateTimeOffset.UtcNow,
                "Machine", null, ProductCode));
            await db.SaveChangesAsync();
        }

        var result = await RetryAsync(client, deploymentId);

        result.Queued.ShouldBe(0);
        (await TaskCountAsync(_fixture, device)).ShouldBe(1);

        var detail = await DetailAsync(client, deploymentId);
        detail.Targets.Single(t => t.Attempt == 2).Reason.ShouldBe("NewerInstalled");
    }

    /// <summary>A device retired since the original run receives nothing.</summary>
    [Fact]
    public async Task Retry_excludes_a_device_retired_since_the_original_deployment()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-RETIRE");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: false, "MSI installation failed.");

        await using (var db = _fixture.CreateDbContext())
        {
            var d = await db.Devices.SingleAsync(x => x.Id == device);
            d.Retire();
            await db.SaveChangesAsync();
        }

        var result = await RetryAsync(client, deploymentId);

        result.Queued.ShouldBe(0);
        (await TaskCountAsync(_fixture, device)).ShouldBe(1);
        (await DetailAsync(client, deploymentId)).Targets
            .Single(t => t.Attempt == 2).Reason.ShouldBe("Retired");
    }

    /// <summary>Withdrawal stops retries as well as new deployments.</summary>
    [Fact]
    public async Task Retry_is_refused_once_the_package_is_withdrawn()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-WITHDRAWN");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: false, "MSI installation failed.");

        (await client.PostAsync(
            new Uri($"/admin/v1/packages/{packageId}/withdraw", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsync(
            new Uri($"/admin/v1/deployments/{deploymentId}/retry", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await TaskCountAsync(_fixture, device)).ShouldBe(1);
    }

    [Fact]
    public async Task Retry_of_a_deployment_with_nothing_failed_queues_nothing()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-ALLOK");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: true, "Installed.");

        var result = await RetryAsync(client, deploymentId);

        result.Targeted.ShouldBe(0);
        result.Queued.ShouldBe(0);
        (await TaskCountAsync(_fixture, device)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_deployment_cannot_be_retried_or_cancelled()
    {
        using var client = await AdminAsync();
        var id = Guid.CreateVersion7();

        (await client.PostAsync(new Uri($"/admin/v1/deployments/{id}/retry", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync(new Uri($"/admin/v1/deployments/{id}/cancel", UriKind.Relative), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_and_cancel_reject_an_unauthenticated_caller()
    {
        using var anonymous = _fixture.Factory.CreateClient();
        var id = Guid.CreateVersion7();

        (await anonymous.PostAsync(new Uri($"/admin/v1/deployments/{id}/retry", UriKind.Relative), null))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await anonymous.PostAsync(new Uri($"/admin/v1/deployments/{id}/cancel", UriKind.Relative), null))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------ idempotency

    /// <summary>
    /// The guard against a double-clicked Deploy: the second request resolves the
    /// same device, finds the install already outstanding, and queues nothing.
    /// </summary>
    [Fact]
    public async Task A_repeated_deploy_request_does_not_queue_a_second_install()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("RTY-DOUBLE");

        await DeployAsync(client, packageId, device);

        var second = await client.PostAsJsonAsync(Deployments, new
        {
            packageId,
            deviceIds = new[] { device },
            groupIds = Array.Empty<Guid>(),
        });

        var result = (await second.Content.ReadFromJsonAsync<CreateResponse>())!;

        result.Queued.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        (await TaskCountAsync(_fixture, device)).ShouldBe(1, "one outstanding install, not two");

        var detail = await DetailAsync(client, result.DeploymentId);
        detail.Targets.Single().Reason.ShouldBe("AlreadyInProgress");
    }

    // ------------------------------------------------------------- cancelling

    /// <summary>Queued work can be cancelled; delivered work cannot.</summary>
    [Fact]
    public async Task Cancel_stops_pending_work_and_leaves_a_delivered_install_running()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var pending = await SeedDeviceAsync("CAN-PENDING");
        var running = await SeedDeviceAsync("CAN-RUNNING");

        var deploymentId = await DeployAsync(client, packageId, pending, running);

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.SingleAsync(
                t => t.DeviceId == running && t.Type == DeviceTaskType.InstallPackage);
            task.TryDeliver(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var cancel = await client.PostAsync(
            new Uri($"/admin/v1/deployments/{deploymentId}/cancel", UriKind.Relative), null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await cancel.Content.ReadFromJsonAsync<CancelResponse>())!;

        result.Considered.ShouldBe(1, "only the still-queued task is a candidate");
        result.Cancelled.ShouldBe(1);

        var detail = await DetailAsync(client, deploymentId);
        detail.Targets.Single(t => t.DeviceId == pending).Status.ShouldBe("Cancelled");
        // Reporting a delivered install as cancelled would be a claim the platform
        // cannot support: it is running on a Windows machine.
        detail.Targets.Single(t => t.DeviceId == running).Status.ShouldBe("Installing");
    }

    [Fact]
    public async Task A_completed_deployment_has_nothing_to_cancel()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("CAN-DONE");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: true, "Installed.");

        var result = (await (await client.PostAsync(
            new Uri($"/admin/v1/deployments/{deploymentId}/cancel", UriKind.Relative), null))
            .Content.ReadFromJsonAsync<CancelResponse>())!;

        result.Considered.ShouldBe(0);
        result.Cancelled.ShouldBe(0);
        (await DetailAsync(client, deploymentId)).Targets.Single().Status.ShouldBe("Succeeded");
    }

    // ---------------------------------------------------------------- offline

    /// <summary>
    /// A device that has not checked in for a long time is waiting, not failing.
    /// Reporting it as Failed would send an operator chasing a fault that does not
    /// exist; reporting it as Pending hides that nothing is coming.
    /// </summary>
    [Fact]
    public async Task A_silent_device_reads_as_offline_rather_than_pending_or_failed()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var offline = await SeedDeviceAsync("OFF-LINE", lastSeen: DateTimeOffset.UtcNow.AddHours(-6));
        var online = await SeedDeviceAsync("OFF-ONLINE");

        var deploymentId = await DeployAsync(client, packageId, offline, online);

        var detail = await DetailAsync(client, deploymentId);

        detail.Targets.Single(t => t.DeviceId == offline).Status.ShouldBe("Offline");
        detail.Targets.Single(t => t.DeviceId == online).Status.ShouldBe("Pending");
        detail.Tally.Offline.ShouldBe(1);
        detail.Tally.Failed.ShouldBe(0);
    }

    /// <summary>An expired task is distinct from a failed one.</summary>
    [Fact]
    public async Task An_expired_task_reads_as_expired_and_is_retryable()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("EXP-DEV");

        var deploymentId = await DeployAsync(client, packageId, device);

        await using (var db = _fixture.CreateDbContext())
        {
            var task = await db.DeviceTasks.SingleAsync(
                t => t.DeviceId == device && t.Type == DeviceTaskType.InstallPackage);
            task.TryExpire(DateTimeOffset.UtcNow.AddHours(2));
            await db.SaveChangesAsync();
        }

        (await DetailAsync(client, deploymentId)).Targets.Single().Status.ShouldBe("Expired");

        var result = await RetryAsync(client, deploymentId);

        result.Queued.ShouldBe(1, "an expired task never ran, so it is worth retrying");
    }

    // ------------------------------------------------------- failure reporting

    /// <summary>
    /// The agent's own concise message is surfaced, so an operator can tell a hash
    /// mismatch from an installer exit code without opening a log.
    /// </summary>
    [Fact]
    public async Task The_agent_failure_message_reaches_the_deployment_detail()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("FAIL-MSG");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(
            device, succeeded: false, "Downloaded package failed its content-hash check; not installed.");

        var target = (await DetailAsync(client, deploymentId)).Targets.Single();

        target.Status.ShouldBe("Failed");
        target.ResultMessage.ShouldNotBeNull();
        target.ResultMessage!.Contains("content-hash", StringComparison.Ordinal).ShouldBeTrue();
        target.ResultMessage.Length.ShouldBeLessThan(500, "concise, not a dumped installer log");
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Retry_and_cancel_are_audited()
    {
        using var client = await AdminAsync();
        var packageId = await RegisterPackageAsync(client, "3.0.0");
        var device = await SeedDeviceAsync("AUD-DEV");

        var deploymentId = await DeployAsync(client, packageId, device);
        await CompleteTaskAsync(device, succeeded: false, "MSI installation failed.");

        await client.PostAsync(new Uri($"/admin/v1/deployments/{deploymentId}/retry", UriKind.Relative), null);
        await client.PostAsync(new Uri($"/admin/v1/deployments/{deploymentId}/cancel", UriKind.Relative), null);

        await using var db = _fixture.CreateDbContext();
        var actions = await db.AuditLogEntries
            .Where(a => a.TargetId == deploymentId.ToString())
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldContain("software.deployment.create");
        actions.ShouldContain("software.deployment.retry");
        actions.ShouldContain("software.deployment.cancel");
    }
}
