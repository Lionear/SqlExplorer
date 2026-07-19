using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SqlExplorer.App.Views;

/// <summary>The "AI activity" tool panel (SE-159) — a read-only feed of MCP audit events. Its DataContext is
/// an <see cref="ViewModels.AiActivityViewModel"/>, set where the panel is mounted (App startup).</summary>
public partial class AiActivityView : UserControl
{
    public AiActivityView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
