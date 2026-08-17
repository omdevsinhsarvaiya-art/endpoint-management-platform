using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// One network adapter on a device. The set is replaced on each inventory upload.
/// </summary>
public sealed class DeviceNetworkInterface : AuditableEntity
{
    private DeviceNetworkInterface()
    {
        Name = null!;
    }

    public DeviceNetworkInterface(
        Guid deviceId,
        string name,
        string? macAddress,
        string? ipAddressesJson,
        bool isUp,
        DateTimeOffset collectedAt)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: 256);
        MacAddress = NormalizeMac(macAddress);
        IpAddressesJson = ipAddressesJson;
        IsUp = isUp;
        CollectedAt = collectedAt;
    }

    public Guid DeviceId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Colon-separated uppercase hex, e.g. <c>A1:B2:C3:D4:E5:F6</c>; null for adapters without one.</summary>
    public string? MacAddress { get; private set; }

    /// <summary>JSON array of IP address strings (v4 and v6).</summary>
    public string? IpAddressesJson { get; private set; }

    public bool IsUp { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    private static string? NormalizeMac(string? macAddress)
    {
        var value = Guard.OptionalMaxLength(macAddress, 23);

        if (value is null)
        {
            return null;
        }

        // Accept common formats (AABBCCDDEEFF, AA-BB-.., AA:BB:..) and normalise
        // to colon-separated so equality queries behave.
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

        if (hex.Length is not (12 or 16))
        {
            throw new ArgumentException($"'{value}' is not a recognisable MAC address.", nameof(macAddress));
        }

        return string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }
}
