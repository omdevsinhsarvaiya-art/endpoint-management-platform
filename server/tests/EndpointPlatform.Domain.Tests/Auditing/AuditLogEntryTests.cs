using System.Net;
using System.Reflection;
using EndpointPlatform.Domain.Auditing;

namespace EndpointPlatform.Domain.Tests.Auditing;

public sealed class AuditLogEntryTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builder_captures_the_full_actor_action_target_result_shape()
    {
        var actorId = Guid.CreateVersion7();
        var deviceId = Guid.CreateVersion7();

        var entry = AuditLogEntry.For(
                OrganizationId,
                OccurredAt,
                AuditActorType.PlatformUser,
                actorId,
                "admin@company.local",
                "user.change_account_type",
                AuditResult.Success)
            .OnDevice(deviceId, "PC-023")
            .OnTarget("windows_local_user", "S-1-5-21-1", "john.smith")
            .WithStateChange("""{"accountType":"StandardUser"}""", """{"accountType":"Administrator"}""")
            .Requiring("user.change_type")
            .FromRequest(IPAddress.Parse("10.20.30.40"), "Mozilla/5.0", "corr-123")
            .Build();

        entry.OrganizationId.ShouldBe(OrganizationId);
        entry.OccurredAt.ShouldBe(OccurredAt);
        entry.ActorType.ShouldBe(AuditActorType.PlatformUser);
        entry.ActorId.ShouldBe(actorId);
        entry.ActorDisplay.ShouldBe("admin@company.local");
        entry.Action.ShouldBe("user.change_account_type");
        entry.Result.ShouldBe(AuditResult.Success);
        entry.DeviceId.ShouldBe(deviceId);
        entry.DeviceDisplay.ShouldBe("PC-023");
        entry.TargetType.ShouldBe("windows_local_user");
        entry.TargetDisplay.ShouldBe("john.smith");
        entry.PreviousState.ShouldBe("""{"accountType":"StandardUser"}""");
        entry.NewState.ShouldBe("""{"accountType":"Administrator"}""");
        entry.RequiredPermission.ShouldBe("user.change_type");
        entry.SourceIp.ShouldBe(IPAddress.Parse("10.20.30.40"));
        entry.CorrelationId.ShouldBe("corr-123");
    }

    /// <summary>
    /// The type must expose no way to change an entry after construction.
    /// </summary>
    /// <remarks>
    /// Reflection is the right tool here: a hand-written assertion would only cover
    /// the properties that exist today, whereas this fails the moment someone adds a
    /// public setter or a mutating method to the audit record.
    /// </remarks>
    [Fact]
    public void Audit_entries_expose_no_public_mutator()
    {
        var type = typeof(AuditLogEntry);

        var publicSetters = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToArray();

        publicSetters.ShouldBeEmpty(
            "AuditLogEntry must be append-only; a public setter would allow a written entry to be altered.");

        var mutatingMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        mutatingMethods.ShouldBeEmpty(
            "AuditLogEntry must expose no instance methods that could modify a written entry.");
    }

    [Fact]
    public void Action_is_normalised_to_lowercase()
    {
        var entry = Build("User.Change_Account_Type");

        entry.Action.ShouldBe("user.change_account_type");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Action_is_required(string? action)
    {
        Should.Throw<ArgumentException>(() => Build(action!));
    }

    [Fact]
    public void Actor_display_is_required_even_for_anonymous_actors()
    {
        // A denied, unauthenticated attempt still has to say something about who
        // tried; an audit entry with no actor at all is not investigable.
        Should.Throw<ArgumentException>(() => AuditLogEntry.For(
            OrganizationId,
            OccurredAt,
            AuditActorType.Anonymous,
            actorId: null,
            actorDisplay: "  ",
            action: "auth.sign_in",
            result: AuditResult.Denied));
    }

    [Fact]
    public void Denied_results_can_record_the_permission_that_was_missing()
    {
        var entry = AuditLogEntry.For(
                OrganizationId,
                OccurredAt,
                AuditActorType.PlatformUser,
                Guid.CreateVersion7(),
                "helpdesk@company.local",
                "device.shutdown",
                AuditResult.Denied)
            .Requiring("device.shutdown")
            .WithFailureReason("Caller lacks required permission.")
            .Build();

        entry.Result.ShouldBe(AuditResult.Denied);
        entry.RequiredPermission.ShouldBe("device.shutdown");
        entry.FailureReason.ShouldBe("Caller lacks required permission.");
    }

    [Fact]
    public void Over_long_optional_fields_are_rejected_rather_than_silently_truncated()
    {
        // Silent truncation would corrupt the record; the database columns are
        // bounded, so oversize input must be a caller error.
        Should.Throw<ArgumentException>(() =>
            AuditLogEntry.For(
                    OrganizationId, OccurredAt, AuditActorType.System, null, "system", "test.action",
                    AuditResult.Success)
                .OnTarget("type", new string('x', 257), "display")
                .Build());
    }

    [Fact]
    public void Empty_optional_strings_are_stored_as_null()
    {
        var entry = AuditLogEntry.For(
                OrganizationId, OccurredAt, AuditActorType.System, null, "system", "test.action",
                AuditResult.Success)
            .OnTarget("   ", "", null)
            .Build();

        entry.TargetType.ShouldBeNull();
        entry.TargetId.ShouldBeNull();
        entry.TargetDisplay.ShouldBeNull();
    }

    private static AuditLogEntry Build(string action) =>
        AuditLogEntry.For(
                OrganizationId,
                OccurredAt,
                AuditActorType.System,
                actorId: null,
                actorDisplay: "system",
                action: action,
                result: AuditResult.Success)
            .Build();
}
