namespace Lionear.SqlExplorer.Sdk.Shortcuts;

/// <summary>
/// Optional capability a plugin (provider or tool) may implement to register global keyboard shortcuts.
/// The host discovers it with an <c>is</c>-check at load, exactly like <c>IPluginSettings</c>, and shows
/// the declared shortcuts in Settings ▸ Keyboard. A plugin that doesn't implement this contributes no
/// shortcuts. Purely declarative aside from the execute delegate, so it crosses the plugin ALC boundary
/// cleanly.
/// </summary>
public interface IShortcutContributor
{
    /// <summary>The shortcuts this plugin contributes; an empty list means "none".</summary>
    IReadOnlyList<ShortcutContribution> Shortcuts { get; }
}
