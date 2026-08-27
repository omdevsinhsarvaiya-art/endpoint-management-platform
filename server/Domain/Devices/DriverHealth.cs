namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// How a device's driver is faring, as judged from the Windows PnP problem code.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately four states rather than a boolean. <see cref="Unknown"/> is not
/// <see cref="Healthy"/>: an agent that could not read a devnode's status has told
/// us nothing about it, and rendering that as healthy would manufacture reassurance
/// out of missing evidence.
/// </para>
/// <para>
/// <see cref="Disabled"/> exists because this platform disables devices itself.
/// USB storage restriction (Milestone 11a) works by setting
/// <c>CM_PROB_DISABLED</c> on a devnode, so every restricted stick on every managed
/// endpoint reports a problem code. Folding that into <see cref="Problem"/> would
/// make the platform's own correct behaviour look like a fleet-wide driver fault,
/// and would bury real faults under it.
/// </para>
/// </remarks>
public enum DriverHealthState
{
    /// <summary>The devnode reports no problem.</summary>
    Healthy = 0,

    /// <summary>The devnode reports a problem code that indicates a real fault.</summary>
    Problem = 1,

    /// <summary>
    /// Administratively disabled (<c>CM_PROB_DISABLED</c>). Not a fault: somebody,
    /// possibly this platform, turned the device off on purpose.
    /// </summary>
    Disabled = 2,

    /// <summary>The problem state could not be read. Never treated as healthy.</summary>
    Unknown = 3,
}

/// <summary>
/// What a problem is attributable to, kept distinct because the remedy differs.
/// </summary>
/// <remarks>
/// A driver fault is something an administrator can act on from here -- reinstall,
/// update, re-enable a service. A device fault usually is not: the hardware is
/// absent, failing, or in resource conflict, and pushing a driver at it will not
/// help. Collapsing the two would send operators to the wrong remedy, so the
/// distinction survives all the way to the console.
/// </remarks>
public enum DriverFaultKind
{
    /// <summary>No fault to attribute.</summary>
    None = 0,

    /// <summary>The driver software is at fault: missing, unloadable, blocked, misconfigured.</summary>
    Driver = 1,

    /// <summary>The hardware is at fault or absent: not present, failing, in conflict.</summary>
    Device = 2,

    /// <summary>
    /// A real problem whose attribution is genuinely ambiguous. Reported honestly
    /// rather than guessed, because a wrong attribution is worse than none.
    /// </summary>
    Indeterminate = 3,
}

/// <summary>One device's driver health verdict.</summary>
/// <param name="State">Healthy, Problem, Disabled or Unknown.</param>
/// <param name="FaultKind">What the problem is attributable to.</param>
/// <param name="ProblemCode">The raw Windows problem code, or null when unread.</param>
/// <param name="Description">
/// Plain-language description of the problem code. Fixed text drawn from the
/// documented meaning of the code -- never a message the endpoint composed, so it
/// cannot become an injection path into the console.
/// </param>
public readonly record struct DriverHealthVerdict(
    DriverHealthState State,
    DriverFaultKind FaultKind,
    int? ProblemCode,
    string Description)
{
    /// <summary>Whether this verdict counts against the device's driver health.</summary>
    /// <remarks>
    /// Disabled does not count: it is an intended state. Unknown does not count
    /// either, but it is reported separately and never silently folded into the
    /// healthy total.
    /// </remarks>
    public bool CountsAsFault => State == DriverHealthState.Problem;
}

/// <summary>
/// Maps a Windows PnP problem code to a health verdict.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is deliberately incomplete. Codes whose attribution is documented
/// and unambiguous are classified; everything else that indicates a problem is
/// reported as <see cref="DriverFaultKind.Indeterminate"/> rather than being forced
/// into driver-or-device. An operator reading "a problem we did not attribute" can
/// investigate; an operator reading a confidently wrong attribution cannot.
/// </para>
/// <para>
/// Codes are the <c>CM_PROB_*</c> values from cfgmgr32.h, which surface in Device
/// Manager as "Code nn".
/// </para>
/// </remarks>
public static class DriverHealth
{
    // The subset of CM_PROB_* this platform classifies. Named rather than inlined
    // so the mapping below reads as the decision it is.
    private const int NotConfigured = 1;          // No driver installed.
    private const int OutOfMemory = 3;            // Driver may be bad/corrupt.
    private const int InvalidData = 9;            // Invalid device registry data.
    private const int FailedStart = 10;           // Device cannot start. Ambiguous.
    private const int NormalConflict = 12;        // Resource conflict.
    private const int NeedRestart = 14;           // Pending restart to take effect.
    private const int Reinstall = 18;             // Drivers must be reinstalled.
    private const int Registry = 19;              // Corrupt driver registry data.
    private const int WillBeRemoved = 21;         // Removal in progress.
    private const int Disabled = 22;              // Administratively disabled.
    private const int DeviceNotThere = 24;        // Device absent/not connected.
    private const int FailedInstall = 28;         // Drivers not installed.
    private const int FailedAdd = 31;             // Windows cannot load the driver.
    private const int DisabledService = 32;       // Driver's start type disabled.
    private const int DriverFailedPriorUnload = 37;
    private const int DriverFailedLoad = 38;      // Previous instance still in memory.
    private const int DriverServiceKeyInvalid = 39;
    private const int LegacyServiceNoDevices = 41;
    private const int Halted = 43;                // Hardware reported a problem.
    private const int Phantom = 45;               // Not currently connected.
    private const int DriverBlocked = 48;         // Known-bad driver, blocked.
    private const int UnsignedDriver = 52;        // Signature could not be verified.

    /// <summary>
    /// Classifies a reported problem code.
    /// </summary>
    /// <param name="problemCode">
    /// The Windows problem code: 0 for no problem, null when the agent could not
    /// read it. Null yields <see cref="DriverHealthState.Unknown"/> -- never healthy.
    /// </param>
    public static DriverHealthVerdict Classify(int? problemCode) => problemCode switch
    {
        null => new(DriverHealthState.Unknown, DriverFaultKind.None, null,
            "The endpoint did not report a problem state for this device."),

        0 => new(DriverHealthState.Healthy, DriverFaultKind.None, 0,
            "This device is working properly."),

        // Intended, not broken. Kept out of the fault counts entirely -- this is
        // also the state Milestone 11a's USB restriction produces.
        Disabled => new(DriverHealthState.Disabled, DriverFaultKind.None, Disabled,
            "This device is disabled."),

        // ---- Attributable to the driver ------------------------------------
        NotConfigured => Driver(NotConfigured, "No driver is installed for this device."),
        OutOfMemory => Driver(OutOfMemory, "The driver could not be loaded; it may be corrupt or out of memory."),
        InvalidData => Driver(InvalidData, "The device's registry data is invalid."),
        Reinstall => Driver(Reinstall, "The drivers for this device must be reinstalled."),
        Registry => Driver(Registry, "The device's driver registry data is corrupt."),
        FailedInstall => Driver(FailedInstall, "The drivers for this device are not installed."),
        FailedAdd => Driver(FailedAdd, "Windows cannot load the drivers required for this device."),
        DisabledService => Driver(DisabledService, "The driver's service is disabled and cannot start."),
        DriverFailedPriorUnload => Driver(DriverFailedPriorUnload,
            "A previous instance of the driver is still in memory."),
        DriverFailedLoad => Driver(DriverFailedLoad,
            "Windows cannot load the driver because a previous instance is still in memory."),
        DriverServiceKeyInvalid => Driver(DriverServiceKeyInvalid,
            "The driver's service registry key is invalid."),
        LegacyServiceNoDevices => Driver(LegacyServiceNoDevices,
            "The driver loaded but Windows cannot find the hardware it manages."),
        DriverBlocked => Driver(DriverBlocked,
            "This driver is blocked from starting because it is known to have problems with Windows."),
        UnsignedDriver => Driver(UnsignedDriver,
            "The driver's digital signature could not be verified."),

        // ---- Attributable to the hardware ----------------------------------
        NormalConflict => Device(NormalConflict, "This device cannot find enough free resources to use."),
        WillBeRemoved => Device(WillBeRemoved, "Windows is removing this device."),
        DeviceNotThere => Device(DeviceNotThere, "This device is not present or is not connected."),
        Halted => Device(Halted, "Windows stopped this device because it reported a problem."),
        Phantom => Device(Phantom, "This device is not currently connected to the computer."),

        // ---- Real problems we decline to attribute -------------------------
        // Code 10 in particular is the most common and the least diagnostic: a
        // device that "cannot start" may have a bad driver or bad hardware, and
        // Windows does not say which.
        FailedStart => Indeterminate(FailedStart, "This device cannot start."),
        NeedRestart => Indeterminate(NeedRestart,
            "This device will not work properly until the computer is restarted."),

        _ => Indeterminate(problemCode.Value,
            $"This device is reporting Windows problem code {problemCode.Value}."),
    };

    private static DriverHealthVerdict Driver(int code, string description) =>
        new(DriverHealthState.Problem, DriverFaultKind.Driver, code, description);

    private static DriverHealthVerdict Device(int code, string description) =>
        new(DriverHealthState.Problem, DriverFaultKind.Device, code, description);

    private static DriverHealthVerdict Indeterminate(int code, string description) =>
        new(DriverHealthState.Problem, DriverFaultKind.Indeterminate, code, description);
}
