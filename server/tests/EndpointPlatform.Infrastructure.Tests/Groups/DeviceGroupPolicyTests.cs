using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Groups;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Policies;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointPlatform.Infrastructure.Tests.Groups;

/// <summary>
/// Group membership + group-targeted policy resolution against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeviceGroupPolicyTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static AuditWriter Audit(Infrastructure.Persistence.EndpointPlatformDbContext db) =>
        new(db, TimeProvider.System, new CorrelationIdAccessor(), new HttpContextAccessor());

    [Fact]
    public async Task A_group_targeted_policy_is_effective_for_group_members_only()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("G", ("g" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t", Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"), Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var inGroup = Device.Enroll(org.Id, "IN", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        var outGroup = Device.Enroll(org.Id, "OUT", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.AddRange(inGroup, outGroup);
        await db.SaveChangesAsync();

        var groupService = new DeviceGroupService(db, Audit(db), TimeProvider.System);
        var group = await groupService.CreateAsync(org.Id, "Finance", "d", Guid.CreateVersion7(), "admin", CancellationToken.None);
        await groupService.AddMemberAsync(org.Id, group.Id, inGroup.Id, Guid.CreateVersion7(), "admin", CancellationToken.None);

        var policyService = new PolicyService(db, Audit(db), TimeProvider.System);
        var policy = await policyService.CreateAsync(
            org.Id, PolicyType.ScreenLockTimeout, "Lock", "d", """{"maxTimeoutSeconds":600}""",
            Guid.CreateVersion7(), "admin", CancellationToken.None);

        // Assign to the GROUP, not the device.
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, policy.Id, PolicyAssignmentTarget.Group, group.Id));
        await db.SaveChangesAsync();

        var forMember = await policyService.GetEffectivePoliciesAsync(inGroup.Id, CancellationToken.None);
        var forNonMember = await policyService.GetEffectivePoliciesAsync(outGroup.Id, CancellationToken.None);

        forMember.ShouldHaveSingleItem().Policy.Id.ShouldBe(policy.Id);
        forNonMember.ShouldBeEmpty("a group-targeted policy must not reach non-members");
    }

    [Fact]
    public async Task Removing_a_member_removes_the_group_policy_from_it()
    {
        await using var db = _fixture.CreateDbContext();
        var org = new Organization("G2", ("h" + Guid.CreateVersion7().ToString("N")).Substring(0, 18));
        db.Organizations.Add(org);
        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t", Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"), Guid.CreateVersion7(), "a@b", DateTimeOffset.UtcNow.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);
        var device = Device.Enroll(org.Id, "D", "m-" + Guid.CreateVersion7().ToString("N"), "1", null, token.Id, DateTimeOffset.UtcNow);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var groupService = new DeviceGroupService(db, Audit(db), TimeProvider.System);
        var group = await groupService.CreateAsync(org.Id, "HR", "d", Guid.CreateVersion7(), "admin", CancellationToken.None);
        await groupService.AddMemberAsync(org.Id, group.Id, device.Id, Guid.CreateVersion7(), "admin", CancellationToken.None);

        var policyService = new PolicyService(db, Audit(db), TimeProvider.System);
        var policy = await policyService.CreateAsync(org.Id, PolicyType.ScreenLockTimeout, "L", "d", """{"maxTimeoutSeconds":300}""", Guid.CreateVersion7(), "admin", CancellationToken.None);
        db.PolicyAssignments.Add(new PolicyAssignment(org.Id, policy.Id, PolicyAssignmentTarget.Group, group.Id));
        await db.SaveChangesAsync();

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None)).Count.ShouldBe(1);

        await groupService.RemoveMemberAsync(org.Id, group.Id, device.Id, Guid.CreateVersion7(), "admin", CancellationToken.None);

        (await policyService.GetEffectivePoliciesAsync(device.Id, CancellationToken.None))
            .ShouldBeEmpty("removing the device from the group removes the group's policy from it");
    }
}
