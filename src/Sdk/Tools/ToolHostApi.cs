namespace Lionear.SqlExplorer.Sdk.Tools;

/// <summary>
/// Versioning gate between the host and tool plugins, separate from <c>ProviderHostApi</c> so the two
/// plugin kinds evolve independently. A tool's <c>plugin.json</c> declares the version it was built for;
/// the loader refuses one this host cannot satisfy.
/// </summary>
public static class ToolHostApi
{
    public const int Version = 1;

    public static bool IsCompatible(int pluginVersion) => pluginVersion == Version;
}
