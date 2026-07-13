using Lionear.SqlExplorer.Sdk.Branding;

namespace Lionear.SqlExplorer.Sdk.Tools;

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

    /// <summary>Menu-item / dialog title (e.g. "Backup…").</summary>
    string Title { get; }

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
