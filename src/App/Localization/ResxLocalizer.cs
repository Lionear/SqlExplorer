using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Lionear.SqlExplorer.Core.Localization;

namespace Lionear.SqlExplorer.App.Localization;

public sealed class ResxLocalizer : ILocalizer
{
    private readonly ResourceManager _resources =
        new("Lionear.SqlExplorer.App.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    public string this[string key] => _resources.GetString(key, _culture) ?? key;

    public string Get(string key, params object[] args)
    {
        var format = this[key];
        return args.Length == 0 ? format : string.Format(_culture, format, args);
    }

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Null property name signals "everything changed" — refreshes the indexer bindings.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
