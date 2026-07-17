namespace SqlExplorer.Core.Providers;

/// <summary>Formatting for the engine label shown in the status bar and connect message.</summary>
public static class ProviderLabel
{
    /// <summary>"{displayName} {version}" when the server version is known (host-API v25), else the
    /// display name alone — the fallback for providers that report no version.</summary>
    public static string Engine(string displayName, string? version) =>
        string.IsNullOrWhiteSpace(version) ? displayName : $"{displayName} {version}";
}
