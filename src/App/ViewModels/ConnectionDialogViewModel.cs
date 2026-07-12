using System.Collections.ObjectModel;
using System.ComponentModel;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the "new connection" dialog. The field list is rebuilt from the selected provider's
/// declared <see cref="ConnectionField"/>s — the dialog itself knows nothing provider-specific.
/// </summary>
public partial class ConnectionDialogViewModel : ViewModelBase
{
    private readonly ConnectionService _connections;
    private readonly IDbProviderRegistry _providers;
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = "New connection";

    [ObservableProperty]
    private ProviderOption? _selectedProvider;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialogTitle))]
    private bool _isEditing;

    /// <summary>Selected accent for this connection (hex, or null for none). Drives the tree flag.</summary>
    [ObservableProperty]
    private string? _color;

    /// <summary>Safe mode: block the editable-grid save-flow for this connection.</summary>
    [ObservableProperty]
    private bool _readOnly;

    // A small fixed palette + "none"; enough to flag prod/staging without a full colour picker.
    private static readonly string?[] Palette =
        [null, "#E5484D", "#F76B15", "#FFB224", "#30A46C", "#3574F0", "#8E4EC6"];

    public ConnectionDialogViewModel(ConnectionService connections, IDbProviderRegistry providers, ILocalizer localizer)
    {
        _connections = connections;
        _providers = providers;
        Loc = localizer;

        AvailableProviders = providers.All
            .Select(r => new ProviderOption(r.Id, r.Provider.DisplayName))
            .OrderBy(o => o.DisplayName)
            .ToList();
        _selectedProvider = AvailableProviders.FirstOrDefault();
        ColorOptions = Palette.Select(c => new ColorSwatch(c)).ToList();
        OnColorChanged(_color);
        RebuildFields();
    }

    /// <summary>The selectable colour swatches (first is "none").</summary>
    public IReadOnlyList<ColorSwatch> ColorOptions { get; }

    // Keep the swatch selection ring in sync with the chosen colour.
    partial void OnColorChanged(string? value)
    {
        foreach (var swatch in ColorOptions)
        {
            swatch.IsSelected = string.Equals(swatch.Value, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void SelectColor(ColorSwatch? swatch) => Color = swatch?.Value;

    public ILocalizer Loc { get; }

    public IReadOnlyList<ProviderOption> AvailableProviders { get; }

    public ObservableCollection<ConnectionFieldInput> Fields { get; } = [];

    public string DialogTitle => IsEditing ? Loc["EditConnection"] : Loc["NewConnection"];

    /// <summary>Save is allowed once there is a name, a provider, and every required field is filled.</summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name)
        && SelectedProvider is not null
        && Fields.All(f => !f.Field.Required || !string.IsNullOrWhiteSpace(f.Value));

    /// <summary>Switch the dialog to edit an existing connection: prefill everything, keep its id.</summary>
    public void LoadForEdit(SavedConnection connection)
    {
        _id = connection.Id;
        IsEditing = true;
        Name = connection.Name;
        Color = connection.Color;
        ReadOnly = connection.ReadOnly;
        SelectedProvider = AvailableProviders.FirstOrDefault(o => o.Id == connection.ProviderId) ?? SelectedProvider;

        // Ensure fields match the provider even if SelectedProvider didn't change, then overlay stored values
        // (secrets pulled back from the keychain).
        RebuildFields();
        var values = _connections.GetEditableValues(connection);
        foreach (var field in Fields)
        {
            if (values.TryGetValue(field.Field.Key, out var value))
            {
                field.Value = value;
            }
        }
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        RebuildFields();
        OnPropertyChanged(nameof(CanSave));
    }

    private void RebuildFields()
    {
        foreach (var field in Fields)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Fields.Clear();
        if (SelectedProvider is null)
        {
            return;
        }

        foreach (var field in _providers.Get(SelectedProvider.Id).ConnectionFields)
        {
            var input = new ConnectionFieldInput(field);
            input.PropertyChanged += OnFieldChanged;
            Fields.Add(input);
        }
    }

    // A field's value changed -> the required-fields check may flip, so re-evaluate the Save gate.
    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionFieldInput.Value))
        {
            OnPropertyChanged(nameof(CanSave));
        }
    }

    private Dictionary<string, string?> Values() =>
        Fields.ToDictionary(f => f.Field.Key, f => f.Value);

    [RelayCommand]
    private async Task TestAsync(CancellationToken ct)
    {
        if (SelectedProvider is not { } option)
        {
            return;
        }

        try
        {
            var profile = _connections.BuildProfile(Name, option.Id, Values());
            var ok = await _providers.Get(option.Id).TestConnectionAsync(profile, ct);
            TestResult = ok ? Loc["TestOk"] : Loc["TestFailed"];
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
        }
    }

    /// <summary>Persist and return the saved connection (secrets go to the keychain).</summary>
    public SavedConnection Save() => _connections.Save(_id, Name, SelectedProvider!.Id, Values(), Color, ReadOnly);
}

/// <summary>A selectable provider in the connection dialog: the manifest id plus its friendly label.</summary>
public sealed record ProviderOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One colour choice in the connection dialog's swatch row (<see cref="Value"/> null = none).</summary>
public sealed partial class ColorSwatch(string? value) : ObservableObject
{
    public string? Value { get; } = value;

    [ObservableProperty]
    private bool _isSelected;
}
