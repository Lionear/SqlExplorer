using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Sdk.Tools;

/// <summary>
/// A tool plugin: contributes a UI + an action to the host, rather than a database engine. The host
/// renders a generic dialog from <see cref="Fields"/> (Route A) — or the plugin's own view when it also
/// implements <c>ICustomToolUi</c> (Route B) — collects the inputs, and calls <see cref="ExecuteAsync"/>
/// with a <see cref="ToolExecutionContext"/> for the selected connection/node. Compiled, desktop-only
/// (Layer B), same trust assumptions as providers.
/// </summary>
public interface IToolPlugin
{
    /// <summary>Stable id (one assembly may ship several tools, so this need not match the manifest id).</summary>
    string Id { get; }

    /// <summary>Menu-item label — the leaf shown in the (possibly nested) tools menu, e.g. "Database…"
    /// under a "Shrink" submenu.</summary>
    string Title { get; }

    /// <summary>Optional localization key for <see cref="Title"/>. When set and the plugin ships a matching
    /// translation, the host shows it instead of <see cref="Title"/>; otherwise <see cref="Title"/> stays
    /// the (English) fallback. Additive — a plugin that ignores it renders exactly as before.</summary>
    string? TitleKey => null;

    /// <summary>Title for the tool's dialog window/header. Defaults to <see cref="Title"/>, but a tool whose
    /// menu label is a short leaf (e.g. "Database…" under "Shrink") can give the dialog a fuller standalone
    /// name (e.g. "Shrink Database").</summary>
    string DialogTitle => Title;

    /// <summary>Optional localization key for <see cref="DialogTitle"/> (same rule as <see cref="TitleKey"/>).</summary>
    string? DialogTitleKey => null;

    /// <summary>Optional explanatory text shown at the top of the tool's dialog, above its fields — what the
    /// tool does and, for an asymmetric tool, which side it changes (e.g. "changes the target database to
    /// match the one you pick"). Null (the default) shows nothing. Additive.</summary>
    string? Description => null;

    /// <summary>Optional localization key for <see cref="Description"/> (same rule as <see cref="TitleKey"/>).</summary>
    string? DescriptionKey => null;

    /// <summary>
    /// Optional submenu path this tool lives under, as ordered ancestor labels (e.g. <c>["Shrink"]</c> to
    /// show it as <c>Tools ▸ Shrink ▸ {Title}</c>, or <c>["Maintenance", "Shrink"]</c> for a deeper nest).
    /// Empty (the default) places the tool directly under the Tools menu. Tools sharing a path — even from
    /// different plugins — merge into the same submenu, so a plugin can contribute the whole stack.
    /// </summary>
    IReadOnlyList<string> MenuPath => [];

    ProviderIcon? Icon => null;

    /// <summary>Where in the tree this tool is offered.</summary>
    ToolTarget Target { get; }

    /// <summary>The inputs the host renders and collects (Route A).</summary>
    IReadOnlyList<ToolField> Fields { get; }

    /// <summary>When true the host shows a destructive-action confirmation before running (e.g. restore).</summary>
    bool IsDestructive => false;

    /// <summary>
    /// Optionally produce a short summary for a chosen file the moment its <see cref="ToolFieldType.File"/>
    /// field changes (e.g. read a backup's plaintext header), shown under that field before Execute runs.
    /// Return null (the default) for no preview. <paramref name="filePath"/> is the current field value.
    /// </summary>
    Task<string?> PreviewAsync(string filePath, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// Run the tool. <paramref name="inputs"/> holds the collected field values keyed by
    /// <see cref="ToolField.Key"/>; report progress lines through <paramref name="progress"/>.
    /// </summary>
    Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct);
}
