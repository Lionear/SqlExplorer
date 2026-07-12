using System.ComponentModel;
using System.Globalization;

namespace Lionear.SqlExplorer.Core.Localization;

/// <summary>
/// UI-independent localization seam. Implements <see cref="INotifyPropertyChanged"/>
/// so bindings refresh when the culture switches at runtime (see Notes.md §7).
/// The indexer is the binding entry point: <c>Loc[Run]</c>.
/// </summary>
public interface ILocalizer : INotifyPropertyChanged
{
    CultureInfo Culture { get; }

    string this[string key] { get; }

    string Get(string key, params object[] args);

    void SetCulture(CultureInfo culture);
}
