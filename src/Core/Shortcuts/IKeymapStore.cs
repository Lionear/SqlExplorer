using System.Collections.Generic;

namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// Persists only the user's <em>overrides</em> keyed by command id — a command left at its factory
/// default is absent from the map. A present entry with a <c>null</c> value means the user deliberately
/// unbound the command (distinct from "never touched"). Defaults live in <see cref="ShortcutCatalog"/>.
/// </summary>
public interface IKeymapStore
{
    IReadOnlyDictionary<string, string?> Load();

    void Save(IReadOnlyDictionary<string, string?> overrides);
}
