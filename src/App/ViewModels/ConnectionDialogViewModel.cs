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

    public ConnectionDialogViewModel(ConnectionService connections, IDbProviderRegistry providers, ILocalizer localizer)
    {
        _connections = connections;
        _providers = providers;
        Loc = localizer;

        AvailableProviders = providers.All
            .Select(p => new ProviderOption(p.Kind, p.DisplayName))
            .OrderBy(o => o.DisplayName)
            .ToList();
        _selectedProvider = AvailableProviders.FirstOrDefault();
        RebuildFields();
    }

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
        SelectedProvider = AvailableProviders.FirstOrDefault(o => o.Kind == connection.Kind) ?? SelectedProvider;

        // Ensure fields match the kind even if SelectedProvider didn't change, then overlay stored values
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

        foreach (var field in _providers.Get(SelectedProvider.Kind).ConnectionFields)
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
            var profile = _connections.BuildProfile(Name, option.Kind, Values());
            var ok = await _providers.Get(option.Kind).TestConnectionAsync(profile, ct);
            TestResult = ok ? Loc["TestOk"] : Loc["TestFailed"];
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
        }
    }

    /// <summary>Persist and return the saved connection (secrets go to the keychain).</summary>
    public SavedConnection Save() => _connections.Save(_id, Name, SelectedProvider!.Kind, Values());
}

/// <summary>A selectable provider in the connection dialog: the engine plus its friendly label.</summary>
public sealed record ProviderOption(DatabaseKind Kind, string DisplayName)
{
    public override string ToString() => DisplayName;
}
