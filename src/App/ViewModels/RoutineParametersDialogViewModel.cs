using System.Collections.ObjectModel;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Sdk.Routines;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Collects the IN parameter values for a procedure/function's "Execute…" flow. It does not run anything:
/// "Open in Editor" hands the values back so the provider can build a call script, which opens in an
/// editable query tab for the user to run themselves. OUT/return parameters are shown disabled so it's
/// clear they are captured by the generated script, not entered here.
/// </summary>
public partial class RoutineParametersDialogViewModel(ILocalizer localizer) : ViewModelBase
{
    public ILocalizer Loc { get; } = localizer;

    [ObservableProperty]
    private string _title = string.Empty;

    public ObservableCollection<RoutineParameterInput> Parameters { get; } = [];

    /// <summary>True once the user chose "Open in Editor" (vs. cancelling).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Set by the view; closes the window.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>Prepare the dialog for one routine. <paramref name="routineName"/> titles the window.</summary>
    public void Configure(string routineName, IReadOnlyList<RoutineParameter> parameters)
    {
        Title = routineName;
        foreach (var parameter in parameters)
        {
            Parameters.Add(new RoutineParameterInput(parameter));
        }
    }

    /// <summary>The user-entered IN values, keyed by parameter name (OUT/return rows are excluded).</summary>
    public IReadOnlyDictionary<string, string?> Values =>
        Parameters.Where(p => !p.IsOutput).ToDictionary(p => p.Name, p => p.Value);

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke();
    }
}

/// <summary>One editable row in the routine parameter dialog. IN parameters take a value; OUT/return
/// parameters are shown read-only with an "(output)" hint.</summary>
public partial class RoutineParameterInput : ObservableObject
{
    public RoutineParameterInput(RoutineParameter parameter)
    {
        Name = parameter.Name;
        Type = parameter.Type;
        IsOutput = parameter.IsOutput;
        Value = parameter.Default;
    }

    public string Name { get; }

    public string Type { get; }

    public bool IsOutput { get; }

    public bool IsInput => !IsOutput;

    [ObservableProperty]
    private string? _value;
}
