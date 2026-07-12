using System.Diagnostics;
using System.Text;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Secrets;

/// <summary>
/// Linux backend over the freedesktop Secret Service (gnome-keyring / kwallet) via the
/// <c>secret-tool</c> CLI from libsecret — the same vault GNOME/KDE use. Requires a running
/// secret service; throws a clear error otherwise rather than falling back to plaintext.
/// </summary>
/// <remarks>
/// Verified on Fedora 44 + GNOME. A native libsecret P/Invoke could replace the CLI later,
/// but this keeps the integration robust and dependency-light.
/// </remarks>
public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string Service = "com.lionear.sqlexplorer";

    public void Set(string key, string secret)
    {
        // secret-tool store reads the password from stdin.
        var psi = NewStartInfo("store", "--label", "Lionear SQL Explorer", "service", Service, "account", key);
        psi.RedirectStandardInput = true;

        using var process = Start(psi);
        process.StandardInput.Write(secret);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"secret-tool store failed (exit {process.ExitCode}): {error.Trim()}");
        }
    }

    public string? Get(string key)
    {
        var psi = NewStartInfo("lookup", "service", Service, "account", key);

        using var process = Start(psi);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Exit 1 = not found; secret-tool prints the secret without a trailing newline.
        return process.ExitCode == 0 ? output : null;
    }

    public void Delete(string key)
    {
        var psi = NewStartInfo("clear", "service", Service, "account", key);
        using var process = Start(psi);
        process.WaitForExit();
    }

    private static ProcessStartInfo NewStartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start secret-tool.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "secret-tool (libsecret) is required for secure credential storage on Linux.", ex);
        }
    }
}
