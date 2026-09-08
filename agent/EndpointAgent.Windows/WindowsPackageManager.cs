using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgent.Windows;

/// <summary>
/// The AppX deployment engine's remove operation, reached through raw Windows
/// Runtime activation of <c>Windows.Management.Deployment.PackageManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// No projection. The agent targets plain <c>net10.0-windows</c> on purpose (see
/// the project file), so the class is activated with <c>RoActivateInstance</c>
/// and its interfaces are declared here by hand, IID and member order copied
/// from the Windows SDK headers named on each one. A wrong slot would call the
/// wrong method, which is why every interface declares only the members up to
/// the one it needs and says which header it was read from. The runtime lays
/// out IUnknown-based interfaces only -- it refuses <c>IInspectable</c> as a
/// base -- so the three <c>IInspectable</c> slots (<c>GetIids</c>,
/// <c>GetRuntimeClassName</c>, <c>GetTrustLevel</c>) are held by placeholders
/// on every interface; the ABI is the same either way.
/// </para>
/// <para>
/// This is a typed call into a Windows service, not a command: the one variable
/// is a package full name, and the engine itself decides what that names. No
/// process is launched (ADR-0005). The operation is asynchronous by design; it
/// is waited for here by polling <c>IAsyncInfo.Status</c>, with a bound, so a
/// hung deployment cannot hold the task pipeline forever. Every wrapper and
/// string handle is released on the way out, whatever happened.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsPackageManager
{
    private const string RuntimeClassName = "Windows.Management.Deployment.PackageManager";

    /// <summary>
    /// <c>RemovalOptions.RemoveForAllUsers</c> (windows.management.deployment.h):
    /// the package goes for every account on the device, not only the caller's.
    /// </summary>
    private const uint RemoveForAllUsers = 0x80000;

    private const int RoInitMultithreaded = 1;                      // RO_INIT_MULTITHREADED
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ENoInterface = unchecked((int)0x80004002);

    // AsyncStatus (asyncinfo.h).
    private const int AsyncStarted = 0;
    private const int AsyncCompleted = 1;

    /// <summary>HRESULT_FROM_WIN32(ERROR_INSTALL_PACKAGE_NOT_FOUND): nothing by that full name is registered.</summary>
    internal const int PackageNotFound = unchecked((int)0x80073CF1);

    /// <summary>HRESULT_FROM_WIN32(ERROR_TIMEOUT): the engine outlived the wait.</summary>
    internal const int TimedOut = unchecked((int)0x800705B4);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Removes a package for all users and waits, up to <paramref name="timeout"/>,
    /// for the engine's verdict.
    /// </summary>
    internal static Task<PackageRemovalReport> RemoveForAllUsersAsync(
        string packageFullName, TimeSpan timeout, CancellationToken cancellationToken) =>
        // A thread of its own: RoInitialize and RoUninitialize are per-thread and
        // must bracket every pointer used in between, and the wait blocks for as
        // long as the engine takes. Neither belongs on a pool thread.
        Task.Factory.StartNew(
            () => Remove(packageFullName, timeout, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static PackageRemovalReport Remove(string packageFullName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var initialised = Initialise();
        try
        {
            return RemoveOnThisThread(packageFullName, timeout, cancellationToken);
        }
        finally
        {
            if (initialised)
            {
                NativeMethods.RoUninitialize();
            }
        }
    }

    /// <summary>Whether this call owns an initialisation that must be balanced.</summary>
    private static bool Initialise()
    {
        var hr = NativeMethods.RoInitialize(RoInitMultithreaded);
        if (hr == 0 || hr == SFalse)
        {
            // S_FALSE: the thread was already initialised. Still balanced by
            // RoUninitialize, per the contract.
            return true;
        }

        if (hr == RpcEChangedMode)
        {
            // The thread already belongs to another apartment. Usable as it is,
            // and not ours to uninitialise.
            return false;
        }

        throw new COMException("The Windows Runtime could not be initialised for package removal.", hr);
    }

    private static PackageRemovalReport RemoveOnThisThread(
        string packageFullName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var className = IntPtr.Zero;
        var fullName = IntPtr.Zero;
        object? manager = null;
        object? operation = null;
        object? result = null;

        try
        {
            className = CreateString(RuntimeClassName);
            fullName = CreateString(packageFullName);

            Check(NativeMethods.RoActivateInstance(className, out var instance), "activating the package manager");
            manager = Adopt(instance);

            Check(
                As<IPackageManager2>(manager).RemovePackageWithOptionsAsync(fullName, RemoveForAllUsers, out var started),
                "starting the removal");
            operation = Adopt(started);

            var info = As<IAsyncInfo>(operation);
            var status = Wait(info, timeout, cancellationToken);

            if (status == AsyncStarted)
            {
                // Still running past the bound. Ask it to stop and let go; the
                // engine keeps its own state, and the task result says so.
                _ = info.Cancel();
                _ = info.Close();
                return new PackageRemovalReport(
                    false, TimedOut, $"the deployment engine did not finish within {timeout.TotalMinutes:0} minutes");
            }

            _ = info.GetErrorCode(out var code);

            // The deployment result is readable in every terminal state, and on
            // failure it is the better source: the extended error code is the
            // specific reason and the text is the engine's own sentence about it.
            string? errorText = null;
            if (As<IAsyncOperationWithProgressOfDeploymentResult>(operation).GetResults(out var produced) >= 0
                && produced != IntPtr.Zero)
            {
                result = Adopt(produced);
                var deployment = As<IDeploymentResult>(result);
                if (deployment.GetExtendedErrorCode(out var extended) >= 0 && extended != 0)
                {
                    code = extended;
                }

                if (deployment.GetErrorText(out var text) >= 0)
                {
                    errorText = ReadString(text);
                    DeleteString(text);
                }
            }

            _ = info.Close();

            return new PackageRemovalReport(status == AsyncCompleted, code, errorText);
        }
        finally
        {
            Release(result);
            Release(operation);
            Release(manager);
            DeleteString(fullName);
            DeleteString(className);
        }
    }

    /// <summary>The terminal status, or <see cref="AsyncStarted"/> when the bound ran out first.</summary>
    private static int Wait(IAsyncInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            Check(info.GetStatus(out var status), "reading the removal status");
            if (status != AsyncStarted)
            {
                return status;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _ = info.Cancel();
                _ = info.Close();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Environment.TickCount64 >= deadline)
            {
                return AsyncStarted;
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>Wraps a raw interface pointer and gives up the caller's reference to it.</summary>
    private static object Adopt(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            throw new COMException("The deployment engine returned no object.", ENoInterface);
        }

        try
        {
            return Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            // The wrapper holds its own reference; this one was the callee's gift.
            _ = Marshal.Release(pointer);
        }
    }

    /// <summary>Queries the wrapper for an interface, naming it when the engine does not have it.</summary>
    private static T As<T>(object wrapper) where T : class
    {
        try
        {
            return (T)wrapper;
        }
        catch (InvalidCastException)
        {
            throw new COMException($"The deployment engine does not expose {typeof(T).Name}.", ENoInterface);
        }
    }

    private static void Release(object? wrapper)
    {
        if (wrapper is not null && Marshal.IsComObject(wrapper))
        {
            _ = Marshal.FinalReleaseComObject(wrapper);
        }
    }

    private static IntPtr CreateString(string value)
    {
        Check(NativeMethods.WindowsCreateString(value, (uint)value.Length, out var hstring), "creating a string");
        return hstring;
    }

    private static void DeleteString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero)
        {
            _ = NativeMethods.WindowsDeleteString(hstring);
        }
    }

    private static string? ReadString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero)
        {
            return null; // An empty HSTRING is a null handle.
        }

        var buffer = NativeMethods.WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero || length == 0 ? null : Marshal.PtrToStringUni(buffer, (int)length);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"The deployment engine failed while {what}.", hr);
        }
    }

    /// <summary>
    /// <c>IPackageManager2</c> (windows.management.deployment.h, IID
    /// f7aad08d-0840-46f2-b5d8-cad47693a095). Its first member after
    /// IInspectable -- slot 6 -- is <c>RemovePackageWithOptionsAsync(HSTRING,
    /// RemovalOptions, IAsyncOperationWithProgress**)</c>; the rest are not
    /// declared because nothing here calls them.
    /// </summary>
    [ComImport]
    [Guid("F7AAD08D-0840-46F2-B5D8-CAD47693A095")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPackageManager2
    {
        // IInspectable, slots 3-5. Placeholders; never called.
        [PreserveSig]
        int GetIids(out uint count, out IntPtr iids);

        [PreserveSig]
        int GetRuntimeClassName(out IntPtr className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int RemovePackageWithOptionsAsync(IntPtr packageFullName, uint removalOptions, out IntPtr deploymentOperation);
    }

    /// <summary>
    /// <c>IAsyncOperationWithProgress&lt;DeploymentResult, DeploymentProgress&gt;</c>:
    /// the parameterised-interface IID (5a97aab7-b6ea-55ac-a5dc-d5b164d94e94) is
    /// the specialisation in windows.management.deployment.h; the member order is
    /// <c>IAsyncOperationWithProgress_impl</c> in windows.foundation.collections.h
    /// -- put_Progress, get_Progress, put_Completed, get_Completed, GetResults.
    /// Only <c>GetResults</c> (slot 10) is called; the handler members are
    /// declared to hold their slots.
    /// </summary>
    [ComImport]
    [Guid("5A97AAB7-B6EA-55AC-A5DC-D5B164D94E94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAsyncOperationWithProgressOfDeploymentResult
    {
        // IInspectable, slots 3-5. Placeholders; never called.
        [PreserveSig]
        int GetIids(out uint count, out IntPtr iids);

        [PreserveSig]
        int GetRuntimeClassName(out IntPtr className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int PutProgress(IntPtr handler);

        [PreserveSig]
        int GetProgress(out IntPtr handler);

        [PreserveSig]
        int PutCompleted(IntPtr handler);

        [PreserveSig]
        int GetCompleted(out IntPtr handler);

        [PreserveSig]
        int GetResults(out IntPtr results);
    }

    /// <summary>
    /// <c>IAsyncInfo</c> (asyncinfo.h, IID 00000036-0000-0000-C000-000000000046):
    /// get_Id, get_Status, get_ErrorCode, Cancel, Close.
    /// </summary>
    [ComImport]
    [Guid("00000036-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAsyncInfo
    {
        // IInspectable, slots 3-5. Placeholders; never called.
        [PreserveSig]
        int GetIids(out uint count, out IntPtr iids);

        [PreserveSig]
        int GetRuntimeClassName(out IntPtr className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int GetId(out uint id);

        [PreserveSig]
        int GetStatus(out int status);

        [PreserveSig]
        int GetErrorCode(out int errorCode);

        [PreserveSig]
        int Cancel();

        [PreserveSig]
        int Close();
    }

    /// <summary>
    /// <c>IDeploymentResult</c> (windows.management.deployment.h, IID
    /// 2563b9ae-b77d-4c1f-8a7b-20e6ad515ef3): get_ErrorText, get_ActivityId,
    /// get_ExtendedErrorCode.
    /// </summary>
    [ComImport]
    [Guid("2563B9AE-B77D-4C1F-8A7B-20E6AD515EF3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeploymentResult
    {
        // IInspectable, slots 3-5. Placeholders; never called.
        [PreserveSig]
        int GetIids(out uint count, out IntPtr iids);

        [PreserveSig]
        int GetRuntimeClassName(out IntPtr className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int GetErrorText(out IntPtr errorText);

        [PreserveSig]
        int GetActivityId(out Guid activityId);

        [PreserveSig]
        int GetExtendedErrorCode(out int extendedErrorCode);
    }

    private static class NativeMethods
    {
        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern int RoInitialize(int initType);

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern void RoUninitialize();

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

        // HSTRING creation copies the characters; the length is in UTF-16 units.
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        internal static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }
}

/// <summary>What the deployment engine said about a removal.</summary>
/// <param name="Completed">
/// Whether the engine ran the operation to completion, as opposed to ending in
/// error, being cancelled, or outliving the wait.
/// </param>
/// <param name="Code">
/// The engine's HRESULT: the extended error code when it produced one, else the
/// operation's own error code; zero when the removal succeeded.
/// </param>
/// <param name="ErrorText">The engine's own description of a failure, when it gave one.</param>
internal sealed record PackageRemovalReport(bool Completed, int Code, string? ErrorText);
