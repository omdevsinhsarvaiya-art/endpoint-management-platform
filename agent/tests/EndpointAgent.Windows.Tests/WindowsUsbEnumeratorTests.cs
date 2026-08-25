using EndpointAgent.Core.Abstractions;
using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The USB enumerator against this machine's real device tree.
/// </summary>
/// <remarks>
/// <para>
/// Enumeration is read-only: SetupAPI queries and devnode walks, no state
/// changed anywhere. That makes it safe to run on a developer machine or a CI
/// runner, unlike the enforcement half, which disables real hardware and is
/// exercised only on a designated endpoint.
/// </para>
/// <para>
/// The assertions are invariants rather than values — a CI runner has a
/// different set of USB devices from a laptop, and a test that expected a
/// particular stick would be a test that only passes on one desk.
/// </para>
/// </remarks>
public sealed class WindowsUsbEnumeratorTests
{
    private static WindowsUsbDeviceEnumerator Create() =>
        new(NullLogger<WindowsUsbDeviceEnumerator>.Instance);

    [Fact]
    public void Enumeration_succeeds_and_returns_well_formed_instance_ids()
    {
        var devices = Create().Enumerate();

        // Not asserting a count: a machine can legitimately have zero USB
        // devices. What must hold is that anything returned is usable as an
        // enforcement key.
        foreach (var device in devices)
        {
            device.InstanceId.ShouldNotBeNullOrWhiteSpace();
            device.InstanceId.ShouldStartWith(@"USB\", Case.Insensitive);
            device.InstanceId.Length.ShouldBeLessThanOrEqualTo(512);
            Enum.IsDefined(device.Class).ShouldBeTrue();
        }
    }

    [Fact]
    public void Instance_ids_are_unique_so_policy_can_key_off_them()
    {
        var devices = Create().Enumerate();

        // A duplicate would mean two physical devices sharing one policy key —
        // grant access to one and the other inherits it.
        devices.Select(d => d.InstanceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(devices.Count);
    }

    [Fact]
    public void Vendor_and_product_ids_are_four_hex_digits_when_present()
    {
        foreach (var device in Create().Enumerate())
        {
            foreach (var id in new[] { device.VendorId, device.ProductId })
            {
                if (id is null)
                {
                    continue;
                }

                id.Length.ShouldBe(4);
                id.All(Uri.IsHexDigit).ShouldBeTrue($"'{id}' is not hex.");
            }
        }
    }

    /// <summary>
    /// Enumeration is repeatable, which is what makes reconciliation idempotent.
    /// </summary>
    [Fact]
    public void Two_enumerations_in_a_row_agree()
    {
        var first = Create().Enumerate().Select(d => d.InstanceId).OrderBy(s => s).ToArray();
        var second = Create().Enumerate().Select(d => d.InstanceId).OrderBy(s => s).ToArray();

        second.ShouldBe(first);
    }

    // ---- instance-id parsing ----------------------------------------------

    [Theory]
    [InlineData(@"USB\VID_0781&PID_5581\ABC123456789", "0781", "5581", "ABC123456789")]
    [InlineData(@"USB\VID_046D&PID_C31C&MI_00\6&1a2b3c&0&0000", "046D", "C31C", null)]
    [InlineData(@"USB\ROOT_HUB30\4&2b1c9d1&0&0", null, null, null)]
    public void Instance_ids_are_split_into_vendor_product_and_serial(
        string instanceId, string? vendorId, string? productId, string? serial)
    {
        var parsed = WindowsUsbDeviceEnumerator.ParseInstanceId(instanceId);

        parsed.VendorId.ShouldBe(vendorId);
        parsed.ProductId.ShouldBe(productId);
        parsed.Serial.ShouldBe(serial);
    }

    /// <summary>
    /// A Windows-generated instance segment is never reported as a serial.
    /// </summary>
    /// <remarks>
    /// Devices with no serial get a synthesised segment that encodes the USB
    /// port path, e.g. <c>7&amp;2f3c1b2&amp;0&amp;2</c>. Treating that as a serial
    /// would produce a grant that follows the port: unplug the approved stick,
    /// plug in any other, and it would inherit the access. The ampersand is the
    /// tell, and this pins the behaviour.
    /// </remarks>
    [Theory]
    [InlineData(@"USB\VID_1234&PID_5678\7&2f3c1b2&0&2")]
    [InlineData(@"USB\VID_1234&PID_5678\5&1a2b3c4&0&1")]
    public void A_port_path_masquerading_as_a_serial_is_reported_as_no_serial(string instanceId)
    {
        WindowsUsbDeviceEnumerator.ParseInstanceId(instanceId).Serial.ShouldBeNull();
    }

    [Fact]
    public void A_genuine_serial_is_kept()
    {
        WindowsUsbDeviceEnumerator.ParseInstanceId(@"USB\VID_0781&PID_5581\4C530001120611104283")
            .Serial.ShouldBe("4C530001120611104283");
    }

    // ---- classification ----------------------------------------------------

    /// <summary>
    /// Storage is recognised from the driver service, not from the device's name.
    /// </summary>
    /// <remarks>
    /// The name is chosen by the device. If classification trusted it, a stick
    /// advertising itself as "USB Keyboard" would sidestep storage policy
    /// entirely — so the check is on <c>USBSTOR</c> / <c>UASPStor</c>, which is
    /// the driver Windows actually bound.
    /// </remarks>
    [Theory]
    [InlineData("USBSTOR")]
    [InlineData("usbstor")]
    [InlineData("UASPStor")]
    public void Mass_storage_drivers_classify_as_storage(string service)
    {
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0781&PID_5581\NOTAREALDEVICE", service, "USBDevice")
            .ShouldBe(UsbClass.Storage);
    }

    [Fact]
    public void A_device_calling_itself_a_keyboard_is_still_storage_if_it_binds_usbstor()
    {
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0000&PID_0000\NOTAREALDEVICE", "USBSTOR", "Keyboard")
            .ShouldBe(UsbClass.Storage);
    }

    [Theory]
    [InlineData("kbdclass", "Keyboard", UsbClass.Keyboard)]
    [InlineData("mouclass", "Mouse", UsbClass.Mouse)]
    [InlineData("rndismp", "Net", UsbClass.NetworkAdapter)]
    [InlineData("USBHUB3", "USB", UsbClass.Hub)]
    public void Other_classes_are_recognised_from_their_class_or_service(
        string service, string deviceClass, UsbClass expected)
    {
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0000&PID_0000\NOTAREALDEVICE", service, deviceClass)
            .ShouldBe(expected);
    }

    [Fact]
    public void A_device_with_nothing_to_go_on_is_unknown_rather_than_guessed()
    {
        // Unknown is the safe landing place: only Storage is grantable, so a
        // device we cannot classify can never be mistaken for one we could open.
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0000&PID_0000\NOTAREALDEVICE", null, null)
            .ShouldBe(UsbClass.Unknown);
    }
}
