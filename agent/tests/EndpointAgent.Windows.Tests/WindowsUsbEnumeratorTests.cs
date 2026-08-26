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

    // ---- a hub is never storage --------------------------------------------

    /// <summary>
    /// A hub is classified from itself, never from what is plugged into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins the fix for a live defect. <c>Classify</c> gathers driver
    /// services from the whole devnode subtree, and a hub's subtree is every
    /// device on the bus. Plugging a USB stick into a laptop therefore made the
    /// <em>root hub</em> collect <c>USBSTOR</c> and classify as storage — so the
    /// agent disabled the hub, disconnecting the webcam, fingerprint reader and
    /// Bluetooth radio along with it, and the stick itself vanished from
    /// inventory because it was now behind a dead hub.
    /// </para>
    /// <para>
    /// The hub check therefore runs before the storage rules and consults only
    /// the device's own instance ID and service.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(@"USB\ROOT_HUB30\4&70DF8FF&0&0", "USBHUB3")]
    [InlineData(@"USB\ROOT_HUB30\4&3AF0ECE5&0&0", null)]
    [InlineData(@"USB\ROOT_HUB20\4&6A987E4&0", "usbhub")]
    [InlineData(@"USB\VID_05E3&PID_0608\5&ABC&0&1", "USBHUB3")]
    public void A_hub_is_classified_as_a_hub_whatever_is_plugged_into_it(string instanceId, string? service)
    {
        WindowsUsbDeviceEnumerator.IsHub(instanceId, service).ShouldBeTrue();

        // Even told outright that a storage driver is present, it stays a hub.
        WindowsUsbDeviceEnumerator.Classify(instanceId, service, "USB", null)
            .ShouldBe(UsbClass.Hub);

        // Storage signals arriving from every other direction still lose to it.
        WindowsUsbDeviceEnumerator.Classify(instanceId, service, "DiskDrive", @"USB\Class_08")
            .ShouldBe(UsbClass.Hub);
    }

    [Theory]
    [InlineData(@"USB\VID_0781&PID_5581\ABC123", "USBSTOR")]
    [InlineData(@"USB\VID_046D&PID_C31C\5&12345&0&1", "kbdhid")]
    public void A_non_hub_is_not_mistaken_for_one(string instanceId, string service)
    {
        WindowsUsbDeviceEnumerator.IsHub(instanceId, service).ShouldBeFalse();
    }

    // ---- the descendant boundary -------------------------------------------

    /// <summary>
    /// The subtree walk stays inside one physical device.
    /// </summary>
    /// <remarks>
    /// Function drivers of the same device (<c>USBSTOR</c>, <c>HID</c>, and the
    /// <c>&amp;MI_</c> interface children of a composite device) are in scope.
    /// Another <c>USB</c>-enumerated device hanging off a hub is not — that is
    /// the crossing that let a hub inherit a stick's class.
    /// </remarks>
    [Theory]
    // Function children of this device: descend.
    [InlineData(@"USB\VID_0781&PID_5581\ABC123", @"USBSTOR\Disk&Ven_SanDisk&Prod_Cruzer\7&1234&0", true)]
    [InlineData(@"USB\VID_046D&PID_C31C\5&1&0", @"HID\VID_046D&PID_C31C\6&2&0", true)]
    [InlineData(@"USB\VID_0781&PID_5581\ABC123", @"SCSI\Disk&Ven_&Prod_\5&1&0", true)]
    // Interfaces of this same composite device: descend.
    [InlineData(@"USB\VID_2B7E&PID_B851\SN0001", @"USB\VID_2B7E&PID_B851&MI_00\6&33918DF9&0&0000", true)]
    // A different device on a hub: do NOT descend.
    [InlineData(@"USB\ROOT_HUB30\4&70DF8FF&0&0", @"USB\VID_0781&PID_5581\ABC123", false)]
    [InlineData(@"USB\ROOT_HUB20\4&6A987E4&0", @"USB\VID_13D3&PID_3571\00E04C000001", false)]
    // A different composite device's interface: do NOT descend.
    [InlineData(@"USB\VID_2B7E&PID_B851\SN0001", @"USB\VID_9999&PID_1111&MI_00\6&1&0&0", false)]
    public void The_subtree_walk_stays_within_one_device(string parent, string child, bool expected)
    {
        WindowsUsbDeviceEnumerator.MayDescendInto(parent, child).ShouldBe(expected);
    }

    // ---- storage stays visible while disabled ------------------------------

    /// <summary>
    /// A restricted stick still classifies as storage.
    /// </summary>
    /// <remarks>
    /// The self-defeating loop this closes: restricting a device disables its
    /// devnode, which unloads the driver and removes the child devnodes — the
    /// exact two signals the classifier used to recognise storage. The device
    /// then looked like an anonymous <c>Other</c>, disappeared from the console's
    /// storage view, and could no longer be granted access. Compatible IDs are
    /// written from the device's own descriptors and survive being disabled.
    /// </remarks>
    [Theory]
    [InlineData(@"USB\Class_08&SubClass_06&Prot_50")]
    [InlineData(@"USB\Class_08&SubClass_06;USB\Class_08")]
    [InlineData(@"USB\Class_08")]
    [InlineData(@"USB\DevClass_00&SubClass_00&Prot_00;USB\Class_08&SubClass_06&Prot_50")]
    public void A_disabled_stick_is_still_storage_by_its_compatible_ids(string compatibleIds)
    {
        WindowsUsbDeviceEnumerator.DeclaresMassStorage(compatibleIds).ShouldBeTrue();

        // No service and no children: exactly the state a restricted device is in.
        WindowsUsbDeviceEnumerator
            .Classify(@"USB\VID_0781&PID_5581\NOTAREALDEVICE", null, "USB", compatibleIds)
            .ShouldBe(UsbClass.Storage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"USB\Class_09&SubClass_00&Prot_01")]
    [InlineData(@"USB\COMPOSITE")]
    [InlineData(@"USB\Class_03&SubClass_01&Prot_01")]
    [InlineData(@"USB\Class_080")]
    public void Nothing_else_is_read_as_mass_storage(string? compatibleIds)
    {
        WindowsUsbDeviceEnumerator.DeclaresMassStorage(compatibleIds).ShouldBeFalse();
    }
}
