using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Peripherals;

/// <param name="Policy">What the console has decided: Restricted or ReadOnly.</param>
/// <param name="EnforcementState">
/// What the endpoint is actually doing about it, as one of <c>Enforced</c>,
/// <c>Pending</c>, <c>Drifted</c>, <c>Failed</c> or <c>NotApplicable</c>. Kept
/// distinct from <paramref name="Policy"/> so the UI cannot imply a control that
/// is not in place — an offline machine shows Pending, not Enforced.
/// </param>
/// <param name="SerialNumber">Null when the device does not expose one. Never fabricated.</param>
public sealed record UsbDeviceView(
    Guid Id,
    string InstanceId,
    string DeviceClass,
    bool IsStorage,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? Manufacturer,
    string? Product,
    bool IsConnected,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DisconnectedAt,
    string Policy,
    DateTimeOffset? PolicyExpiresAt,
    string EnforcementState,
    DateTimeOffset? EnforcedAt,
    string? EnforcementError,
    Guid? LiveRequestId);

public sealed record UsbAccessRequestView(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    Guid UsbDeviceId,
    string InstanceId,
    string? Product,
    string Status,
    string Source,
    string Justification,
    DateTimeOffset RequestedAt,
    string? DecidedByDisplay,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ExpiresAt,
    string? DecisionNote,
    bool IsLive);

/// <summary>
/// Read-side projections for the peripheral console. Query-only: nothing here
/// changes policy, so a view can never be the thing that grants access.
/// </summary>
public sealed class UsbReadService(EndpointPlatformDbContext dbContext, TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Every USB device an endpoint has reported, connected first.</summary>
    public async Task<IReadOnlyList<UsbDeviceView>> ListForDeviceAsync(
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var devices = await _dbContext.UsbDevices
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.OrganizationId == organizationId)
            .OrderByDescending(u => u.IsConnected)
            .ThenBy(u => u.DeviceClass)
            .ThenByDescending(u => u.LastSeenAt)
            .ToListAsync(cancellationToken);

        var liveByUsbId = await _dbContext.UsbAccessRequests
            .AsNoTracking()
            .Where(r => r.DeviceId == deviceId
                && r.Status == UsbAccessRequestStatus.Approved
                && r.ExpiresAt != null
                && r.ExpiresAt > now)
            .Select(r => new { r.UsbDeviceId, r.Id })
            .ToDictionaryAsync(r => r.UsbDeviceId, r => r.Id, cancellationToken);

        return devices.Select(u => new UsbDeviceView(
            u.Id,
            u.InstanceId,
            u.DeviceClass.ToString(),
            u.IsStorage,
            u.VendorId,
            u.ProductId,
            u.SerialNumber,
            u.Manufacturer,
            u.Product,
            u.IsConnected,
            u.FirstSeenAt,
            u.LastSeenAt,
            u.DisconnectedAt,
            u.Policy.ToString(),
            u.PolicyExpiresAt,
            DescribeEnforcement(u),
            u.EnforcedAt,
            u.EnforcementError,
            liveByUsbId.TryGetValue(u.Id, out var requestId) ? requestId : null))
            .ToList();
    }

    /// <summary>
    /// The fleet-wide access ledger: live grants first, then recent history.
    /// </summary>
    public async Task<IReadOnlyList<UsbAccessRequestView>> ListRequestsAsync(
        Guid organizationId,
        bool liveOnly,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var query =
            from r in _dbContext.UsbAccessRequests.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on r.DeviceId equals d.Id
            where r.OrganizationId == organizationId
            select new { Request = r, d.Hostname, d.DisplayName };

        if (liveOnly)
        {
            query = query.Where(x =>
                x.Request.Status == UsbAccessRequestStatus.Approved
                && x.Request.ExpiresAt != null
                && x.Request.ExpiresAt > now);
        }

        var rows = await query
            .OrderByDescending(x => x.Request.RequestedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        var usbIds = rows.Select(x => x.Request.UsbDeviceId).Distinct().ToList();
        var products = await _dbContext.UsbDevices
            .AsNoTracking()
            .Where(u => usbIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Product })
            .ToDictionaryAsync(u => u.Id, u => u.Product, cancellationToken);

        return rows.Select(x => new UsbAccessRequestView(
            x.Request.Id,
            x.Request.DeviceId,
            x.DisplayName ?? x.Hostname,
            x.Request.UsbDeviceId,
            x.Request.InstanceId,
            products.TryGetValue(x.Request.UsbDeviceId, out var product) ? product : null,
            x.Request.Status.ToString(),
            x.Request.Source.ToString(),
            x.Request.Justification,
            x.Request.RequestedAt,
            x.Request.DecidedByDisplay,
            x.Request.DecidedAt,
            x.Request.ExpiresAt,
            x.Request.DecisionNote,
            x.Request.IsLive(now)))
            .ToList();
    }

    /// <summary>
    /// Turns the desired/reported pair into one word an operator can act on.
    /// </summary>
    /// <remarks>
    /// The distinction that matters is <c>Pending</c> versus <c>Drifted</c>.
    /// Pending means the endpoint has not told us anything yet — it may be
    /// offline, or the policy may still be in flight. Drifted means it told us
    /// it is enforcing something other than what was asked, which on a Windows
    /// box usually means a local administrator re-enabled the device by hand.
    /// Collapsing both into "not enforced" would hide the one that needs
    /// investigating.
    /// </remarks>
    private static string DescribeEnforcement(UsbDevice usb)
    {
        if (!usb.IsStorage)
        {
            return "NotApplicable";
        }

        if (usb.EnforcementError is not null)
        {
            return "Failed";
        }

        if (usb.EnforcedPolicy is null)
        {
            return "Pending";
        }

        return usb.EnforcedPolicy == usb.Policy ? "Enforced" : "Drifted";
    }
}
