using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.App.DependencyInjection;

/// <summary>The App-layer <see cref="IHostUi"/> handed to a plugin's panels and menu actions (SE-164): its
/// <see cref="ShowDialogAsync"/> routes to the main window's modal-dialog host. Lives in the App layer because
/// only it owns the window — the Avalonia-free Core never sees this.</summary>
public sealed class SubsystemHostUi(
    Func<string, Control, Task> showDialog, Func<string, string, Task<bool>> confirm) : IHostUi
{
    public Task ShowDialogAsync(string title, Control content) => showDialog(title, content);

    public Task<bool> ConfirmAsync(string title, string message) => confirm(title, message);
}
