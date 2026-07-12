namespace Lionear.SqlExplorer.Sdk;

/// <summary>How a <see cref="ConnectionField"/> is captured — the host renders one control per type.</summary>
public enum ConnectionFieldType
{
    Text,
    Password,
    Number,
    File,
    Bool
}

/// <summary>
/// One field in a provider's connection form. Providers declare these; the host renders a generic
/// dialog from them and never sees provider-specific UI. Purely declarative so it crosses the ALC
/// boundary cleanly and can later feed a declarative (iOS-safe) provider too.
/// </summary>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldType Type,
    bool Required = false,
    string? Default = null,
    string? Placeholder = null)
{
    /// <summary>Secret values go to the OS keychain, never to the connection config file.</summary>
    public bool IsSecret => Type == ConnectionFieldType.Password;
}
