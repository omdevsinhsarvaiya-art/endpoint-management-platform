using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

public sealed class DeviceInventoryTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static Device EnrollDevice() =>
        Device.Enroll(OrganizationId, "PC-023", "smbios-1", "0.1.0", "Windows 11", Guid.CreateVersion7(), Now);

    [Fact]
    public void A_new_device_has_an_inventory_refresh_pending()
    {
        // Nothing has ever been collected, so the first heartbeat must trigger one.
        EnrollDevice().IsInventoryRefreshPending.ShouldBeTrue();
    }

    [Fact]
    public void Recording_inventory_clears_the_pending_state()
    {
        var device = EnrollDevice();

        device.RecordInventory(@"CORP\jsmith", Now.AddMinutes(1));

        device.IsInventoryRefreshPending.ShouldBeFalse();
        device.LoggedOnUser.ShouldBe(@"CORP\jsmith");
        device.InventoryCollectedAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void An_admin_request_after_collection_makes_the_refresh_pending_again()
    {
        var device = EnrollDevice();
        device.RecordInventory(null, Now.AddMinutes(1));

        device.RequestInventoryRefresh(Now.AddMinutes(5));

        device.IsInventoryRefreshPending.ShouldBeTrue();
    }

    [Fact]
    public void A_request_older_than_the_last_collection_is_not_pending()
    {
        var device = EnrollDevice();
        device.RequestInventoryRefresh(Now.AddMinutes(1));

        device.RecordInventory(null, Now.AddMinutes(2));

        device.IsInventoryRefreshPending.ShouldBeFalse(
            "the collection at T+2 satisfied the request from T+1");
    }

    [Fact]
    public void A_retired_device_rejects_inventory()
    {
        var device = EnrollDevice();
        device.Retire();

        Should.Throw<InvalidOperationException>(() => device.RecordInventory(null, Now.AddMinutes(1)));
    }

    [Fact]
    public void Hardware_applies_and_validates_ranges()
    {
        var hardware = new DeviceHardware(Guid.CreateVersion7());

        hardware.Apply(
            "SER123", "Dell Inc.", "Latitude 5450", "Intel Core Ultra 7", 12, 14,
            34_359_738_368, """[{"name":"C:"}]""", Now);

        hardware.SerialNumber.ShouldBe("SER123");
        hardware.CpuPhysicalCores.ShouldBe(12);
        hardware.TotalMemoryBytes.ShouldBe(34_359_738_368);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(5000)]
    public void Implausible_core_counts_are_rejected(int cores)
    {
        var hardware = new DeviceHardware(Guid.CreateVersion7());

        Should.Throw<ArgumentOutOfRangeException>(() =>
            hardware.Apply(null, null, null, null, cores, null, null, null, Now));
    }

    [Theory]
    [InlineData("A1B2C3D4E5F6", "A1:B2:C3:D4:E5:F6")]
    [InlineData("a1-b2-c3-d4-e5-f6", "A1:B2:C3:D4:E5:F6")]
    [InlineData("a1:b2:c3:d4:e5:f6", "A1:B2:C3:D4:E5:F6")]
    public void Mac_addresses_are_normalised_to_colon_separated_uppercase(string input, string expected)
    {
        var nic = new DeviceNetworkInterface(Guid.CreateVersion7(), "Ethernet", input, null, true, Now);

        nic.MacAddress.ShouldBe(expected);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("A1:B2:C3")]
    public void Unrecognisable_mac_addresses_are_rejected(string input)
    {
        Should.Throw<ArgumentException>(() =>
            new DeviceNetworkInterface(Guid.CreateVersion7(), "Ethernet", input, null, true, Now));
    }

    [Fact]
    public void A_missing_mac_address_is_allowed()
    {
        var nic = new DeviceNetworkInterface(Guid.CreateVersion7(), "VPN", null, null, false, Now);

        nic.MacAddress.ShouldBeNull();
    }
}
