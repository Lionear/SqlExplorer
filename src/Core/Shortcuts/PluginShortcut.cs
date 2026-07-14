using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// A plugin-contributed shortcut as the host tracks it: the plugin's <see cref="ShortcutContribution"/>
/// mapped to a namespaced <paramref name="Id"/> (<c>pluginId:localId</c>) plus the owning plugin's id and
/// title (used to group it in the keyboard settings). The App layer builds these from the SDK contract, so
/// Core stays independent of the SDK. Always window-scoped: plugins can't bind editor-only keys.
/// </summary>
public sealed record PluginShortcut(
    string Id,
    string PluginId,
    string PluginTitle,
    string Title,
    string? DefaultGesture,
    Func<CancellationToken, Task> ExecuteAsync);
