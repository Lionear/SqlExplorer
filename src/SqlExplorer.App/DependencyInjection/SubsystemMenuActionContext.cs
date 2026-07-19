using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.App.DependencyInjection;

/// <summary>The App-layer <see cref="IMenuActionContext"/> handed to a plugin's menu action (SE-164 menu
/// seam): its <see cref="ShowDialogAsync"/> routes to the main window's modal-dialog host. Lives in the App
/// layer because only it owns the window — the Avalonia-free Core never sees this.</summary>
public sealed class SubsystemMenuActionContext(Func<string, Control, Task> showDialog) : IMenuActionContext
{
    public Task ShowDialogAsync(string title, Control content) => showDialog(title, content);
}
