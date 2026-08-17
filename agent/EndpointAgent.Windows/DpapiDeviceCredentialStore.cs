using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// Stores the device credential DPAPI-protected at machine scope.
/// </summary>
/// <remarks>
/// <para>
/// Protection layers: (1) DPAPI <see cref="DataProtectionScope.LocalMachine"/> —
/// the blob only decrypts on this machine, so copying the file to another host
/// yields nothing; (2) additional entropy bound to the store format, so another
/// application on the same machine calling <c>Unprotect</c> casually does not
/// succeed; (3) the state directory is created with an explicit ACL granting
/// SYSTEM and Administrators full control and **no inherited access** — a
/// standard user cannot read the blob at all.
/// </para>
/// <para>
/// LocalMachine scope is correct (not CurrentUser): the service runs as
/// LocalSystem, and machine scope is what survives service-account changes. The
/// residual risk — another admin/SYSTEM process on the same machine can decrypt —
/// is acceptable because such a process could equally read the service's memory.
/// </para>
/// <para>
/// A corrupt or undecryptable blob is treated as "not enrolled" (with a warning),
/// never as a crash: the correct recovery is re-enrollment, and the server side
/// revokes the old credential when that happens.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiDeviceCredentialStore : IDeviceCredentialStore
{
    private const string CredentialFileName = "device-credential.bin";

    // Not a secret: entropy binds the blob to this application's store format so
    // unrelated code calling Unprotect(null entropy) fails. Confidentiality comes
    // from DPAPI + the directory ACL.
    private static readonly byte[] Entropy = "EndpointPlatformAgent.DeviceCredential.v1"u8.ToArray();

    private readonly ILogger<DpapiDeviceCredentialStore> _logger;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DpapiDeviceCredentialStore(
        IOptions<AgentOptions> options,
        ILogger<DpapiDeviceCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _stateDirectory = options.Value.StateDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EndpointPlatformAgent");
    }

    private string CredentialPath => Path.Combine(_stateDirectory, CredentialFileName);

    public async ValueTask<DeviceCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(CredentialPath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(CredentialPath, cancellationToken);

            byte[] plainBytes;
            try
            {
                plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex,
                    "The stored device credential could not be decrypted (corrupt, tampered with, or "
                    + "written on another machine). Treating this machine as not enrolled.");
                return null;
            }

            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedCredential>(plainBytes);

                if (persisted is null
                    || persisted.DeviceId == Guid.Empty
                    || string.IsNullOrWhiteSpace(persisted.KeyId)
                    || string.IsNullOrWhiteSpace(persisted.Secret))
                {
                    _logger.LogWarning("The stored device credential is malformed; treating as not enrolled.");
                    return null;
                }

                return new DeviceCredential(persisted.DeviceId, persisted.KeyId, persisted.Secret);
            }
            finally
            {
                // Plaintext credential bytes should not linger on the heap longer
                // than necessary.
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask SaveAsync(DeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            EnsureSecuredStateDirectory();

            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedCredential(credential.DeviceId, credential.KeyId, credential.Secret));

            try
            {
                var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);

                // Write-then-rename so a crash mid-write can never leave a
                // half-written credential file in place.
                var temporaryPath = CredentialPath + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
                File.Move(temporaryPath, CredentialPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }

            _logger.LogInformation(
                "Device credential stored (DPAPI machine scope) in {Directory}.", _stateDirectory);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken) is not null;

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(CredentialPath))
            {
                File.Delete(CredentialPath);
                _logger.LogInformation("Stored device credential removed.");
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Creates the state directory with an explicit DACL: SYSTEM and Administrators
    /// full control, inheritance from the parent disabled, nothing else. Applied on
    /// every save so a manually pre-created directory with sloppy permissions gets
    /// corrected rather than silently trusted.
    /// </summary>
    private void EnsureSecuredStateDirectory()
    {
        var directoryInfo = new DirectoryInfo(_stateDirectory);

        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            security.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // Owner must be explicit too, or a pre-created directory keeps its
            // creator as owner - and owners can rewrite DACLs. Assigning the
            // Administrators group as owner requires SeRestorePrivilege, which the
            // service (SYSTEM/elevated) holds and an unelevated developer does not;
            // the catch below handles that case.
            security.SetOwner(administrators);

            if (!directoryInfo.Exists)
            {
                directoryInfo.Create(security);
            }
            else
            {
                directoryInfo.SetAccessControl(security);
            }
        }
        // InvalidOperationException: SetAccessControl reports the disallowed owner
        // this way on an existing directory, where Create reports IOException.
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or InvalidOperationException
                                       or System.Security.AccessControl.PrivilegeNotHeldException)
        {
            // Running unelevated (development / tests). The credential is still
            // DPAPI machine-scope protected; only the directory-ACL hardening could
            // not be applied. Fall back to a plain directory and say so clearly
            // instead of failing enrollment - in production the service runs as
            // LocalSystem and takes the hardened path above.
            directoryInfo.Create();

            _logger.LogWarning(ex,
                "Could not apply the restrictive ACL to {Directory} (process not elevated?). "
                + "The credential remains DPAPI-protected, but directory permissions were not hardened.",
                _stateDirectory);
        }
    }

    private sealed record PersistedCredential(Guid DeviceId, string KeyId, string Secret);
}
