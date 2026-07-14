using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lionear.SqlExplorer.Tools.MsSqlAdmin;

/// <summary>
/// Route-B view for <see cref="ShrinkDatabaseTool"/>, modelled on SSMS' Shrink Database General page: a
/// live space read-out (queried through <see cref="IToolUiContext.QueryAsync"/> the moment it opens) plus
/// the "reorganize + release" toggle and its max-free-space percentage. Writes its choices back through
/// the context so the tool's <c>ExecuteAsync</c> picks the right DBCC form.
/// </summary>
public sealed class ShrinkDatabaseView : UserControl
{
    public ShrinkDatabaseView(IToolUiContext context)
    {
        // Seed defaults so ExecuteAsync has values even if the user touches nothing.
        context.SetValue(ShrinkDatabaseTool.ReorganizeKey, "false");
        context.SetValue(ShrinkDatabaseTool.TargetPercentKey, "0");

        var database = new TextBlock { Text = "…", FontWeight = FontWeight.SemiBold };
        var allocated = new TextBlock { Text = "…" };
        var free = new TextBlock { Text = "…" };

        var percent = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 99,
            Increment = 1,
            Value = 0,
            IsEnabled = false,
            FormatString = "0",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        percent.ValueChanged += (_, _) =>
            context.SetValue(ShrinkDatabaseTool.TargetPercentKey, ((int)(percent.Value ?? 0)).ToString());

        var reorganize = new CheckBox { Content = "Reorganize files before releasing unused space" };
        reorganize.IsCheckedChanged += (_, _) =>
        {
            var on = reorganize.IsChecked == true;
            context.SetValue(ShrinkDatabaseTool.ReorganizeKey, on ? "true" : "false");
            percent.IsEnabled = on;
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Shrink action", FontWeight = FontWeight.SemiBold },
                SpaceRow("Database", database),
                SpaceRow("Currently allocated space", allocated),
                SpaceRow("Available free space", free),
                new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.25, Margin = new Thickness(0, 6, 0, 6) },
                reorganize,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(24, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Maximum free space after shrink (%)", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 },
                        percent
                    }
                }
            }
        };

        _ = LoadAsync(context, database, allocated, free);
    }

    private static Grid SpaceRow(string label, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        var name = new TextBlock { Text = label, Opacity = 0.7 };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }

    private static async Task LoadAsync(IToolUiContext context, TextBlock database, TextBlock allocated, TextBlock free)
    {
        try
        {
            // Current-database context (profile is already repointed at the target db), so DB_NAME() and
            // sys.database_files + FILEPROPERTY resolve to this database and its data files.
            var result = await context.QueryAsync(
                """
                SELECT
                    DB_NAME(),
                    CAST(SUM(CAST(size AS bigint)) * 8.0 / 1024 AS decimal(18,2)),
                    CAST(SUM(CAST(size - FILEPROPERTY(name, 'SpaceUsed') AS bigint)) * 8.0 / 1024 AS decimal(18,2))
                FROM sys.database_files WHERE type IN (0, 1)
                """,
                CancellationToken.None);

            var row = result.Rows.Count > 0 ? result.Rows[0] : null;
            var name = row?[0] as string;
            var allocMb = row?[1] as decimal?;
            var freeMb = row?[2] as decimal?;
            var pct = allocMb is > 0 && freeMb is { } f ? (double)(f / allocMb.Value) * 100 : 0;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                database.Text = string.IsNullOrEmpty(name) ? "—" : name;
                allocated.Text = allocMb is { } a ? $"{a:N2} MB" : "—";
                free.Text = freeMb is { } fv ? $"{fv:N2} MB ({pct:N0}% of allocated)" : "—";
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => allocated.Text = $"(unavailable: {ex.Message})");
        }
    }
}
