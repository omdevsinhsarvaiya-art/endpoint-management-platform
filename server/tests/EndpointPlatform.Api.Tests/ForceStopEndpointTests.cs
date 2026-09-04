using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Force Stop: stopping a named installed application on one or more devices.
/// </summary>
/// <remarks>
/// <para>
/// The request names an application, never a process. These tests exist mostly to
/// prove what cannot be asked for: no pid, no image name, no executable path,
/// nothing outside the caller's organization, and nothing that would reach a
/// retired device.
/// </para>
/// <para>
/// The server queues exactly one <see cref="DeviceTaskType.StopApplication"/> per
/// device and chooses no pid at all. Which processes that resolves to is settled
/// on the endpoint at execution time, because a pid picked here would come from a
/// snapshot that can be hours old.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ForceStopEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;

    private static readonly Uri ForceStop = new("/admin/v1/software/force-stop", UriKind.Relative);

    private const string ChromeDir = @"C:\Program Files\Google\Chrome\Application";

    /// <summary>The first agent version carrying the StopApplication executor.</summary>
    private const string SupportedAgent = "1.6.0";

    private sealed record DeviceOutcome(Guid DeviceId, string Hostname, string Outcome, int ProcessesQueued);

    private sealed record ForceStopResponse(int ProcessesQueued, IReadOnlyList<DeviceOutcome> Devices);

    private async Task<HttpClient> AdminAsync()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        return _fixture.CreateClientFor(token);
    }

    /// <summary>
    /// A device with Chrome installed, plus a deliberately stale process list.
    /// </summary>
    /// <remarks>
    /// The seeded processes exist to prove they are <em>not</em> used: the server
    /// must not read them, and no pid from them may appear in the queued task.
    /// </remarks>
    private async Task<Guid> SeedAsync(
        string hostname,
        string? installLocation = ChromeDir,
        DeviceStatus status = DeviceStatus.Active,
        string appName = "Google Chrome",
        string agentVersion = SupportedAgent)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"fs-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            await db.PlatformUsers.Select(u => u.Id).FirstAsync(), "force-stop-tests", now.AddHours(1), 1);
        db.EnrollmentTokens.Add(token);

        var device = Device.Enroll(
            organizationId, hostname, $"smbios-{Guid.CreateVersion7()}", agentVersion,
            "Microsoft Windows 11 Pro", token.Id, now);

        if (status == DeviceStatus.Retired)
        {
            device.Retire();
        }

        db.Devices.Add(device);

        db.DeviceSoftware.Add(new DeviceSoftware(
            device.Id, appName, "152.0.1", "Google LLC", null, installLocation, "x86", now,
            "Machine", null, null));

        // A stale snapshot. Nothing here may influence the queued task.
        db.DeviceProcesses.Add(new DeviceProcessEntry(
            device.Id, 4321, "chrome", 100_000, $@"{ChromeDir}\chrome.exe", now));
        db.DeviceProcesses.Add(new DeviceProcessEntry(
            device.Id, 900, "explorer", 100_000, @"C:\Windows\explorer.exe", now));

        await db.SaveChangesAsync();
        return device.Id;
    }

    private static async Task<List<DeviceTask>> StopTasksAsync(AdminApiPostgresFixture f, Guid deviceId)
    {
        await using var db = f.CreateDbContext();
        return await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.StopApplication)
            .ToListAsync();
    }

    private static object Body(IEnumerable<Guid> deviceIds, string name = "Google Chrome", string? publisher = null) =>
        new { deviceIds, name, publisher };

    // ------------------------------------------------------------------- core

    /// <summary>
    /// One task per device, carrying the application and its install directory —
    /// and no pid, because the server does not choose one.
    /// </summary>
    [Fact]
    public async Task Force_stop_queues_one_task_that_names_the_application_not_a_process()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-RUNNING");

        var response = await client.PostAsJsonAsync(ForceStop, Body([device]));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var result = (await response.Content.ReadFromJsonAsync<ForceStopResponse>())!;
        result.Devices.Single().Outcome.ShouldBe("Queued");

        var task = (await StopTasksAsync(_fixture, device)).Single();
        task.PayloadJson.ShouldNotBeNull();

        task.PayloadJson!.Contains("Google Chrome", StringComparison.Ordinal).ShouldBeTrue();
        task.PayloadJson.Contains("Chrome\\\\Application", StringComparison.Ordinal).ShouldBeTrue();

        // The staleness fix, pinned: no pid from the snapshot reaches the endpoint.
        task.PayloadJson.Contains("processId", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        task.PayloadJson.Contains("4321", StringComparison.Ordinal).ShouldBeFalse();
        task.PayloadJson.Contains("explorer", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    /// <summary>
    /// Whether the application is running is the endpoint's determination, made at
    /// execution time. The server queues regardless, because it cannot know — and
    /// the alternative is deciding from a snapshot that may be hours old.
    /// </summary>
    [Fact]
    public async Task The_server_does_not_decide_whether_the_application_is_running()
    {
        using var client = await AdminAsync();

        // No process rows at all: the old implementation reported NotRunning here.
        var device = await SeedAsync("FS-NOPROCS");
        await using (var db = _fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM endpoint_platform.device_processes WHERE device_id = {0}", device);
        }

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.Devices.Single().Outcome.ShouldBe("Queued");
        (await StopTasksAsync(_fixture, device)).Count.ShouldBe(1);
    }

    /// <summary>
    /// Without an install location there is no evidence linking the application to
    /// any process, so Force Stop is unavailable rather than guessed.
    /// </summary>
    [Fact]
    public async Task An_application_with_no_install_location_is_reported_unresolvable()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-NOPATH", installLocation: null);

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.Devices.Single().Outcome.ShouldBe("Unresolvable");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// An over-broad install location is refused before a task is queued. The
    /// endpoint refuses it again; neither side trusts the other to have checked.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Users")]
    public async Task An_application_registered_against_a_system_root_queues_nothing(string location)
    {
        using var client = await AdminAsync();
        var device = await SeedAsync($"FS-BAD-{Guid.CreateVersion7():N}"[..12], installLocation: location);

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.ProcessesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("Unresolvable");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_device_without_the_application_is_reported_as_not_installed()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-OTHER", appName: "Something Else");

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.Devices.Single().Outcome.ShouldBe("NotInstalled");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Several_devices_are_handled_in_one_request()
    {
        using var client = await AdminAsync();
        var a = await SeedAsync("FS-MULTI-A");
        var b = await SeedAsync("FS-MULTI-B", installLocation: null);

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([a, b])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.Devices.Count.ShouldBe(2);
        result.Devices.Single(d => d.DeviceId == a).Outcome.ShouldBe("Queued");
        result.Devices.Single(d => d.DeviceId == b).Outcome.ShouldBe("Unresolvable");
        result.ProcessesQueued.ShouldBe(1);
    }

    // ------------------------------------------------------- agent capability

    /// <summary>
    /// Resolution happens on the endpoint, so an agent without the executor cannot
    /// do it. Refused before a row exists rather than delivered and reported as an
    /// unknown task type, which reads exactly like the application failing to stop.
    /// </summary>
    [Fact]
    public async Task An_agent_without_the_executor_is_reported_ineligible_not_failed()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-OLDAGENT", agentVersion: "1.5.0");

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.ProcessesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("NotEligible");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    // --------------------------------------------------------------- security

    /// <summary>Retired devices receive no tasks of any kind.</summary>
    [Fact]
    public async Task A_retired_device_receives_no_task()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-RETIRED", status: DeviceStatus.Retired);

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.ProcessesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("NotEligible");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    /// <summary>
    /// An unknown device id resolves to nothing rather than being refused, so the
    /// response cannot be used to discover which devices exist.
    /// </summary>
    [Fact]
    public async Task An_unknown_device_id_resolves_to_no_devices()
    {
        using var client = await AdminAsync();

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([Guid.CreateVersion7()])))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.Devices.ShouldBeEmpty();
        result.ProcessesQueued.ShouldBe(0);
    }

    [Fact]
    public async Task A_request_without_an_application_or_a_device_is_refused()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-VALIDATION");

        (await client.PostAsJsonAsync(ForceStop, new { deviceIds = new[] { device }, name = "" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync(ForceStop, new { deviceIds = Array.Empty<Guid>(), name = "Google Chrome" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_force_stop_anything()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        (await anonymous.PostAsJsonAsync(ForceStop, Body([Guid.CreateVersion7()])))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The contract accepts an application name and nothing else. A caller cannot
    /// smuggle in a pid, a process name or an executable path, so no request can
    /// ask the fleet to terminate something arbitrary.
    /// </summary>
    [Fact]
    public async Task A_pid_process_name_or_path_supplied_by_the_client_is_ignored()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-INJECT");

        var response = await client.PostAsJsonAsync(ForceStop, new
        {
            deviceIds = new[] { device },
            name = "Google Chrome",
            // None of these are part of the contract.
            processName = "explorer.exe",
            executablePath = @"C:\Windows\explorer.exe",
            processId = 900,
            installLocation = @"C:\Windows",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var task = (await StopTasksAsync(_fixture, device)).Single();

        // The install location came from inventory, not from the request body.
        task.PayloadJson!.Contains("explorer", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        task.PayloadJson.Contains("Windows", StringComparison.Ordinal).ShouldBeFalse();
        task.PayloadJson.Contains("Chrome", StringComparison.Ordinal).ShouldBeTrue();
        task.PayloadJson.Contains("900", StringComparison.Ordinal).ShouldBeFalse();
    }

    /// <summary>
    /// Naming a system process as the application finds no installed software of
    /// that name, so nothing is queued.
    /// </summary>
    [Fact]
    public async Task Naming_a_windows_process_as_the_application_queues_nothing()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-SYSNAME");

        var result = (await (await client.PostAsJsonAsync(ForceStop, Body([device], name: "explorer")))
            .Content.ReadFromJsonAsync<ForceStopResponse>())!;

        result.ProcessesQueued.ShouldBe(0);
        result.Devices.Single().Outcome.ShouldBe("NotInstalled");
        (await StopTasksAsync(_fixture, device)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task A_force_stop_is_audited_without_recording_executable_paths()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-AUDIT");

        await client.PostAsJsonAsync(ForceStop, Body([device]));

        await using var db = _fixture.CreateDbContext();
        var entry = await db.AuditLogEntries
            .Where(a => a.Action == "software.application.force_stop")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync();

        entry.TargetDisplay.ShouldBe("Google Chrome");
        entry.NewState.ShouldNotBeNull();
        entry.NewState!.Contains("FS-AUDIT", StringComparison.Ordinal).ShouldBeTrue();
        // Outcomes and hostnames only -- not where the binary lives on disk.
        entry.NewState.Contains(@"C:\", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    /// <summary>
    /// Asking twice queues twice, which is correct: the operator is asking again
    /// because the application is still running. Each task resolves independently
    /// against live state, so a repeat cannot act on anything stale.
    /// </summary>
    [Fact]
    public async Task Repeating_a_force_stop_is_safe()
    {
        using var client = await AdminAsync();
        var device = await SeedAsync("FS-REPEAT");

        await client.PostAsJsonAsync(ForceStop, Body([device]));
        await client.PostAsJsonAsync(ForceStop, Body([device]));

        var tasks = await StopTasksAsync(_fixture, device);
        tasks.Count.ShouldBe(2);
        tasks.ShouldAllBe(t => t.Type == DeviceTaskType.StopApplication);
    }
}
