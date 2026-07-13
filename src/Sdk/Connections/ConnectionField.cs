namespace Lionear.SqlExplorer.Sdk.Connections;

/// <summary>How a <see cref="ConnectionField"/> is captured — the host renders one control per type.</summary>
public enum ConnectionFieldType
{
    Text,
    Password,
    Number,
    File,
    Bool,

    /// <summary>A closed set of options rendered as a dropdown; the picked value is stored verbatim.
    /// Populate <see cref="ConnectionField.Choices"/> with the allowed values (e.g. an SSL mode).</summary>
    Choice
}

/// <summary>
/// One field in a provider's connection form. Providers declare these; the host renders a generic
/// dialog from them and never sees provider-specific UI. Purely declarative so it crosses the ALC
/// boundary cleanly and can later feed a declarative (iOS-safe) provider too.
/// </summary>
/// <param name="Group">Optional section label; fields sharing a group render together. Null groups
/// the field with the other ungrouped basics.</param>
/// <param name="Advanced">When true the host tucks the field into a collapsed "Advanced" section so
/// the common fields stay uncluttered (e.g. SSL/timeout tuning). Basic fields stay always visible.</param>
/// <param name="Choices">Allowed values for a <see cref="ConnectionFieldType.Choice"/> field; ignored
/// for other types.</param>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldType Type,
    bool Required = false,
    string? Default = null,
    string? Placeholder = null,
    string? Group = null,
    bool Advanced = false,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Secret values go to the OS keychain, never to the connection config file.</summary>
    public bool IsSecret => Type == ConnectionFieldType.Password;
}
