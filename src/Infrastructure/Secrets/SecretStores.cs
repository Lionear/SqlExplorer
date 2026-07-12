using System.Runtime.InteropServices;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Secrets;

/// <summary>Picks the OS-native credential vault. Never returns a plaintext fallback.</summary>
public static class SecretStores
{
    public static ISecretStore CreateForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceStore();
        }

        throw new PlatformNotSupportedException(
            $"No secure credential store implemented for this OS ({RuntimeInformation.OSDescription}).");
    }
}
