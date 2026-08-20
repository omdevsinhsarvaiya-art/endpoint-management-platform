using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

/// <summary>
/// The console display name is a label, not an identity.
/// </summary>
/// <remarks>
/// These tests exist because the failure they guard against is silent and
/// expensive: if renaming a device in the console were ever to touch the
/// hostname, the machine identifier or the device id, then a machine would stop
/// resolving to its own record. Re-enrollment would create a duplicate, its
/// history would be orphaned, and nobody would notice until an administrator
/// went looking for a device that had quietly become two.
/// </remarks>
public sealed class DeviceDisplayNameTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly Guid TokenId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static Device EnrollDevice() =>
        Device.Enroll(OrganizationId, "LAPTOP-LVCHEQ2H", "smbios-uuid-1", "1.0.0", "Windows 11 Pro", TokenId, Now);

    [Fact]
    public void A_new_device_has_no_display_name_and_shows_its_hostname()
    {
        var device = EnrollDevice();

        device.DisplayName.ShouldBeNull();
        device.Name.ShouldBe("LAPTOP-LVCHEQ2H");
    }

    [Fact]
    public void Renaming_sets_the_label_and_the_shown_name_follows_it()
    {
        var device = EnrollDevice();

        device.Rename("TAM0149");

        device.DisplayName.ShouldBe("TAM0149");
        device.Name.ShouldBe("TAM0149");
    }

    [Fact]
    public void Renaming_does_not_change_the_windows_hostname()
    {
        var device = EnrollDevice();

        device.Rename("TAM0149");

        device.Hostname.ShouldBe("LAPTOP-LVCHEQ2H");
    }

    [Fact]
    public void Renaming_does_not_change_the_machine_identifier()
    {
        var device = EnrollDevice();

        device.Rename("TAM0149");

        device.MachineIdentifier.ShouldBe("smbios-uuid-1");
    }

    [Fact]
    public void Renaming_does_not_change_the_device_id_or_enrollment_lineage()
    {
        var device = EnrollDevice();
        var id = device.Id;

        device.Rename("HR-Laptop-01");

        device.Id.ShouldBe(id);
        device.EnrolledWithTokenId.ShouldBe(TokenId);
        device.EnrolledAt.ShouldBe(Now);
        device.OrganizationId.ShouldBe(OrganizationId);
    }

    [Fact]
    public void Renaming_leaves_every_other_reported_fact_alone()
    {
        var device = EnrollDevice();

        device.Rename("Accounts-Desk-02");

        device.AgentVersion.ShouldBe("1.0.0");
        device.OperatingSystem.ShouldBe("Windows 11 Pro");
        device.Status.ShouldBe(DeviceStatus.Active);
        device.LastSeenAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clearing_the_label_falls_back_to_the_hostname(string? cleared)
    {
        var device = EnrollDevice();
        device.Rename("TAM0149");

        device.Rename(cleared);

        // Blank normalises to null rather than to an empty string, so a device can
        // never end up displaying nothing at all.
        device.DisplayName.ShouldBeNull();
        device.Name.ShouldBe("LAPTOP-LVCHEQ2H");
    }

    [Fact]
    public void A_label_is_trimmed()
    {
        var device = EnrollDevice();

        device.Rename("  Junagadh-Office-01  ");

        device.DisplayName.ShouldBe("Junagadh-Office-01");
    }

    [Fact]
    public void A_label_longer_than_the_column_is_rejected()
    {
        var device = EnrollDevice();

        Should.Throw<ArgumentException>(() => device.Rename(new string('x', 129)));
    }

    [Fact]
    public void A_label_survives_a_heartbeat_that_reports_a_renamed_windows_host()
    {
        // The real case this protects: Windows gets renamed on the endpoint. The
        // agent reports the new hostname, and the administrator's label must not
        // be overwritten by it -- the whole point of the label is that it is
        // independent of what Windows calls itself.
        var device = EnrollDevice();
        device.Rename("TAM0149");

        device.RecordHeartbeat("LAPTOP-NEWNAME", "1.0.0", "Windows 11 Pro", Now.AddMinutes(5));

        device.Hostname.ShouldBe("LAPTOP-NEWNAME");
        device.DisplayName.ShouldBe("TAM0149");
        device.Name.ShouldBe("TAM0149");
    }

    [Fact]
    public void A_label_survives_re_enrollment_of_the_same_machine()
    {
        // Re-enrollment after an OS reinstall keeps the device row. The label is
        // administrative knowledge about which desk the machine sits on, which a
        // reinstall does not invalidate.
        var device = EnrollDevice();
        device.Rename("TAM0149");
        var newToken = Guid.CreateVersion7();

        device.ReEnroll("LAPTOP-LVCHEQ2H", "1.1.0", "Windows 11 Pro", newToken, Now.AddDays(30));

        device.DisplayName.ShouldBe("TAM0149");
        device.MachineIdentifier.ShouldBe("smbios-uuid-1");
    }
}
