using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// The live keymap: resolves the effective gesture for each command (user override → factory default),
/// and applies edits from the Preferences window. Covers both the built-in <see cref="ShortcutCatalog"/>
/// commands and any plugin-contributed shortcuts registered at construction. Stays UI-agnostic — gestures
/// are strings; the App layer parses them. Raises <see cref="Changed"/> after an apply so the main window
/// can rebuild its key bindings and the editor can re-read its comment shortcut without a restart.
/// </summary>
public sealed class KeymapService
{
    private readonly IKeymapStore _store;
    private readonly IReadOnlyList<PluginShortcut> _pluginShortcuts;
    private Dictionary<string, string?> _overrides;

    public KeymapService(IKeymapStore store, IEnumerable<PluginShortcut>? pluginShortcuts = null)
    {
        _store = store;
        _pluginShortcuts = pluginShortcuts?.ToList() ?? [];
        _overrides = new Dictionary<string, string?>(store.Load());
    }

    /// <summary>Set at composition so editor-scoped controls (created per tab, outside DI) can resolve gestures.</summary>
    public static KeymapService? Current { get; set; }

    /// <summary>Fired after <see cref="Apply"/> persists a change; the effective map has already updated.</summary>
    public event Action? Changed;

    /// <summary>The built-in bindable commands (resx-labelled metadata).</summary>
    public IReadOnlyList<ShortcutCommand> Commands => ShortcutCatalog.All;

    /// <summary>Plugin-contributed shortcuts, grouped by owning plugin in the settings UI.</summary>
    public IReadOnlyList<PluginShortcut> PluginShortcuts => _pluginShortcuts;

    /// <summary>Whether the platform primary modifier is Cmd (macOS) rather than Ctrl (Windows/Linux).</summary>
    public static bool UsesCommandKey { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// The factory-default gesture for a command with the <c>Mod</c> token expanded for this OS, or
    /// <c>null</c> for an unknown command or a plugin shortcut that ships unbound.
    /// </summary>
    public string? DefaultGesture(string commandId)
    {
        var template = DefaultTemplate(commandId);
        return template is null ? null : ExpandPrimaryModifier(template);
    }

    /// <summary>The effective gesture for a command, or <c>null</c> when unbound. Unknown ids also return null.</summary>
    public string? Resolve(string commandId)
    {
        if (_overrides.TryGetValue(commandId, out var overridden))
        {
            return NormalizeEmpty(overridden);
        }

        return DefaultGesture(commandId);
    }

    /// <summary>The action to run for a plugin shortcut id, or <c>null</c> when the id isn't a plugin shortcut.</summary>
    public Func<CancellationToken, Task>? PluginAction(string commandId) =>
        _pluginShortcuts.FirstOrDefault(p => p.Id == commandId)?.ExecuteAsync;

    /// <summary>Replaces the <c>Mod</c> token with the platform primary modifier (Cmd on macOS, else Ctrl).</summary>
    public static string ExpandPrimaryModifier(string gesture) =>
        gesture.Replace(ShortcutCatalog.PrimaryModifierToken, UsesCommandKey ? "Cmd" : "Ctrl");

    /// <summary>
    /// Replaces the whole map from a command-id → gesture snapshot (as edited in the UI). Only entries that
    /// differ from the (expanded) factory default are persisted; a <c>null</c>/empty gesture on a
    /// default-bound command is stored as an explicit unbind. Covers built-in and plugin ids alike.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string?> effectiveGestures)
    {
        var next = new Dictionary<string, string?>();
        foreach (var (id, template) in KnownCommands())
        {
            if (!effectiveGestures.TryGetValue(id, out var gesture))
            {
                continue;
            }

            gesture = NormalizeEmpty(gesture);
            var defaultGesture = template is null ? null : ExpandPrimaryModifier(template);
            if (!string.Equals(gesture, defaultGesture, StringComparison.Ordinal))
            {
                next[id] = gesture;
            }
        }

        _overrides = next;
        _store.Save(next);
        Changed?.Invoke();
    }

    // Every id the keymap knows about, paired with its default gesture template (null = ships unbound).
    private IEnumerable<(string Id, string? Template)> KnownCommands()
    {
        foreach (var command in ShortcutCatalog.All)
        {
            yield return (command.Id, command.DefaultGesture);
        }

        foreach (var plugin in _pluginShortcuts)
        {
            yield return (plugin.Id, plugin.DefaultGesture);
        }
    }

    private string? DefaultTemplate(string commandId)
    {
        var built = ShortcutCatalog.All.FirstOrDefault(c => c.Id == commandId);
        if (built is not null)
        {
            return built.DefaultGesture;
        }

        return _pluginShortcuts.FirstOrDefault(p => p.Id == commandId)?.DefaultGesture;
    }

    private static string? NormalizeEmpty(string? gesture) =>
        string.IsNullOrWhiteSpace(gesture) ? null : gesture;
}
