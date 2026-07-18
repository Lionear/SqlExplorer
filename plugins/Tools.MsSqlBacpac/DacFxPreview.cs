using System.Globalization;
using System.IO;
using Microsoft.SqlServer.Dac;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Reads the always-available header of a <c>.bacpac</c>/<c>.dacpac</c> so the Import/Publish dialogs can
/// show what a file contains before running. A BACPAC exposes almost no managed metadata, so its preview
/// stays honest and file-based; a DACPAC surfaces its application name/version/description.
/// </summary>
internal static class DacFxPreview
{
    public static string Bacpac(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return "File not found.";
        }

        // BacPackage.Load validates the archive; failure here means "not a usable bacpac".
        using (BacPackage.Load(filePath)) { }
        return $"BACPAC file : {info.Name}\n" +
               $"Size        : {FormatSize(info.Length)}\n" +
               $"Imported as : a new database (created on the target server)";
    }

    public static string Dacpac(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return "File not found.";
        }

        using var package = DacPackage.Load(filePath);
        var description = string.IsNullOrWhiteSpace(package.Description) ? null : package.Description;
        return $"Application : {package.Name}\n" +
               $"Version     : {package.Version}\n" +
               (description is null ? string.Empty : $"Description : {description}\n") +
               $"Size        : {FormatSize(info.Length)}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
