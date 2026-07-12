using Avalonia.Controls;
using Avalonia.Interactivity;
using Lionear.SqlExplorer.Core.Localization;

namespace Lionear.SqlExplorer.App.Views;

/// <summary>
/// Shows the INSERT/UPDATE/DELETE statements the save-flow generated so the user can review
/// them before they run as one transaction (Notes §8). Closes with <c>true</c> when applied.
/// </summary>
public partial class SaveReviewDialog : Window
{
    public SaveReviewDialog()
    {
        InitializeComponent();
    }

    public SaveReviewDialog(ILocalizer loc, string sql) : this()
    {
        Title = loc["ReviewTitle"];
        HeaderText.Text = loc["ReviewTitle"];
        SqlText.Text = sql;
        ApplyButton.Content = loc["Apply"];
        CancelButton.Content = loc["Cancel"];
    }

    private void OnApply(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
