using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows-only checks for the MSI installer that are safe to run on a real
/// machine: the read-only product-state query and the signature gate. The actual
/// install path (MsiInstallProduct) is never exercised here - it changes machine
/// state and is proven in EndpointAgent.Core against fakes.
/// </summary>
public sealed class WindowsMsiPackageInstallerTests
{
    private static WindowsMsiPackageInstaller Create() =>
        new(NullLogger<WindowsMsiPackageInstaller>.Instance);

    [Fact]
    public async Task A_random_product_code_is_reported_not_installed()
    {
        // A freshly generated ProductCode is astronomically unlikely to be present;
        // this exercises the real MsiQueryProductState P/Invoke read-only.
        var productCode = Guid.CreateVersion7().ToString("B").ToUpperInvariant();

        (await Create().IsProductInstalledAsync(productCode)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_genuinely_installed_msi_product_is_detected()
    {
        // Find an MSI product actually installed on this machine and assert the real
        // MsiQueryProductState call reports it present. This is what proves the
        // P/Invoke binding is correct - a wrong entry point would report "absent".
        var installedProductCode = FindAnInstalledMsiProductCode();
        if (installedProductCode is null)
        {
            return; // No per-machine MSI product to test against; nothing to assert.
        }

        (await Create().IsProductInstalledAsync(installedProductCode)).ShouldBeTrue(
            $"{installedProductCode} is installed per the registry and must be detected");
    }

    private static string? FindAnInstalledMsiProductCode()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var root in roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out _))
                {
                    continue; // MSI products key by ProductCode GUID.
                }

                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("WindowsInstaller") is int wi && wi == 1)
                {
                    return name; // e.g. "{042FE0BF-...}"
                }
            }
        }

        return null;
    }

    [Fact]
    public async Task An_unsigned_file_is_refused_before_any_install()
    {
        // A plain, unsigned temp file must be rejected by the Authenticode gate -
        // the installer must never run for it.
        var path = Path.Combine(Path.GetTempPath(), $"epa-unsigned-{Guid.CreateVersion7():N}.msi");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]);
        try
        {
            var outcome = await Create().InstallAsync(path, requiredSignerSubject: "CN=Contoso");
            outcome.Result.ShouldBe(EndpointAgent.Core.Abstractions.PackageInstallResult.SignatureRejected);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
