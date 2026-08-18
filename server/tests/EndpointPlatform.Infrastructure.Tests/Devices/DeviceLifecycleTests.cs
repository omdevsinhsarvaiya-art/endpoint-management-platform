using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>
/// Phase 14: offboarding revokes credentials and retires a device (blocking
/// check-in and re-enrollment), and reactivation reverses it against real
/// PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceLifecycleTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, TimeProvider.System, new CorrelationIdAccessor(), new HttpContextAccessor());

    private static async Task<(Organization Org, EnrollmentToken Token)> SeedOrgAndTokenAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db)
    {
        var org = new Organization("L", ("l" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new EnrollmentToken(
            org.Id, "t", SecretGenerator.HashSecret(SecretGenerator.GenerateSecret()),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 99);
        db.EnrollmentTokens.Add(token);
        await db.SaveChangesAsync();
        return (org, token);
    }

    private static AgentEnrollmentService Enrollment(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, Audit(db), TimeProvider.System, NullLogger<AgentEnrollmentService>.Instance);

    [Fact]
    public async Task Offboarding_revokes_credentials_and_retires_the_device()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var secret = await IssueTokenSecretAsync(db, org.Id);
        var machineId = "m-" + Guid.CreateVersion7().ToString("N");

        var enrolled = await Enrollment(db).EnrollAsync(secret, "OFF-PC", machineId, "1.0", null);
        enrolled.Success.ShouldBeTrue();

        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);
        (await service.OffboardAsync(org.Id, enrolled.DeviceId, Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.Success);

        await using var verify = _fixture.CreateDbContext();
        (await verify.Devices.SingleAsync(d => d.Id == enrolled.DeviceId)).Status.ShouldBe(DeviceStatus.Retired);
        (await verify.AgentCredentials.Where(c => c.DeviceId == enrolled.DeviceId).ToListAsync())
            .ShouldAllBe(c => c.RevokedAt != null);
    }

    [Fact]
    public async Task A_retired_device_cannot_re_enroll_until_reactivated()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var machineId = "m-" + Guid.CreateVersion7().ToString("N");

        var first = await Enrollment(db).EnrollAsync(await IssueTokenSecretAsync(db, org.Id), "RE-PC", machineId, "1.0", null);
        first.Success.ShouldBeTrue();

        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);
        await service.OffboardAsync(org.Id, first.DeviceId, Guid.CreateVersion7(), "admin");

        // Same machine, fresh token: re-enrollment is refused while retired.
        var refused = await Enrollment(db).EnrollAsync(await IssueTokenSecretAsync(db, org.Id), "RE-PC", machineId, "1.0", null);
        refused.Success.ShouldBeFalse("a retired device must not re-enroll until an admin reactivates it");

        // Reactivate, then re-enrollment succeeds and re-issues a credential.
        (await service.ReactivateAsync(org.Id, first.DeviceId, Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.Success);

        var reenrolled = await Enrollment(db).EnrollAsync(await IssueTokenSecretAsync(db, org.Id), "RE-PC", machineId, "1.0", null);
        reenrolled.Success.ShouldBeTrue("a reactivated device may re-enroll");

        await using var verify = _fixture.CreateDbContext();
        (await verify.AgentCredentials.CountAsync(c => c.DeviceId == first.DeviceId && c.RevokedAt == null))
            .ShouldBe(1, "re-enrollment issues exactly one fresh active credential");
    }

    [Fact]
    public async Task Offboarding_an_unknown_device_reports_not_found()
    {
        await using var db = _fixture.CreateDbContext();
        var (org, _) = await SeedOrgAndTokenAsync(db);
        var service = new DeviceLifecycleService(db, Audit(db), TimeProvider.System);

        (await service.OffboardAsync(org.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), "admin"))
            .ShouldBe(DeviceLifecycleResult.NotFound);
    }

    /// <summary>Issues a fresh enrollment token and returns its usable secret.</summary>
    private static async Task<string> IssueTokenSecretAsync(
        Infrastructure.Persistence.EndpointPlatformDbContext db, Guid organizationId)
    {
        var secret = SecretGenerator.GenerateSecret();
        db.EnrollmentTokens.Add(new EnrollmentToken(
            organizationId, "t-" + Guid.CreateVersion7().ToString("N"), SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 99));
        await db.SaveChangesAsync();
        return secret;
    }
}
