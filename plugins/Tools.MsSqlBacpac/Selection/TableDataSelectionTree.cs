using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// The table list for <see cref="ExportBacpacView"/>: one <c>[Data]</c> checkbox per table plus a
/// select-all header and a filter box. A data-only cousin of Universal Backup's two-column
/// <c>SelectableObjectTree</c> — BACPAC always exports the full schema, so only per-table <em>data</em>
/// inclusion is selectable (mirrors <see cref="Microsoft.SqlServer.Dac.DacServices.ExportBacpac(string,string,Microsoft.SqlServer.Dac.DacExportOptions,System.Collections.Generic.IEnumerable{System.Tuple{string,string}},System.Threading.CancellationToken)"/>'s <c>tables</c> parameter).
/// </summary>
internal sealed class TableDataSelectionTree
{
    private sealed record Row(string Schema, string Name, CheckBox DataBox, Control Container, string Label);

    // One fixed-width checkbox column so the "Data" header and every row's box line up.
    private const string RowColumns = "52,*";

    private readonly List<Row> _rows = [];
    private readonly StackPanel _list = new() { Spacing = 2 };
    private CheckBox? _allData;

    public TextBox FilterBox { get; } = new() { PlaceholderText = "Filter tables…" };

    public Control TreeHost { get; }

    /// <summary>Raised after any checkbox changes so the view can push the selection into its context.</summary>
    public event Action? Changed;

    public TableDataSelectionTree()
    {
        FilterBox.TextChanged += (_, _) => ApplyFilter(FilterBox.Text);
        // Padding goes on the list's Margin, NOT the ScrollViewer's Padding: Avalonia 12's ScrollViewer
        // excludes its own Padding from Extent while still shifting content by it, stranding the last rows
        // below the reachable scroll range (same clipping bug ObjectTreeScaffold documents).
        _list.Margin = new Thickness(8, 8, 8, 12);
        var host = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinHeight = 120,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _list
            }
        };
        // Theme-aware hairline (a hardcoded light border reads wrong in dark mode).
        host.Bind(Border.BorderBrushProperty, host.GetResourceObservable("SEHairlineBrush"));
        TreeHost = host;
        _list.Children.Add(new TextBlock { Text = "Loading…", Opacity = 0.6 });
    }

    /// <summary>Every table whose Data box is ticked.</summary>
    public IReadOnlyList<(string Schema, string Name)> CheckedTables =>
        _rows.Where(r => r.DataBox.IsChecked == true).Select(r => (r.Schema, r.Name)).ToList();

    /// <summary>True when every table's data is selected — the view then exports "all data" (DacFx null).</summary>
    public bool AllChecked => _rows.Count > 0 && _rows.All(r => r.DataBox.IsChecked == true);

    public void ShowMessage(string text)
    {
        _list.Children.Clear();
        _rows.Clear();
        _allData = null;
        _list.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
    }

    public void SetTables(IReadOnlyList<(string Schema, string Name)> tables)
    {
        _list.Children.Clear();
        _rows.Clear();

        if (tables.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No user tables in this database.", Opacity = 0.6 });
            _allData = null;
            return;
        }

        _allData = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        _allData.IsCheckedChanged += (_, _) =>
        {
            if (_allData.IsChecked is { } v)
            {
                foreach (var r in _rows) r.DataBox.IsChecked = v;
            }
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(RowColumns),
            Margin = new Thickness(0, 0, 0, 4),
            Children =
            {
                _allData,
                Place(new TextBlock { Text = $"Tables ({tables.Count})", FontWeight = FontWeight.SemiBold, Opacity = 0.65, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }, 1, new Thickness(4, 0, 0, 0))
            }
        };
        _list.Children.Add(header);

        foreach (var (schema, name) in tables)
        {
            var dataBox = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
            dataBox.IsCheckedChanged += (_, _) => Changed?.Invoke();

            var label = string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
            var labelBlock = new TextBlock
            {
                Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(labelBlock, label);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(RowColumns),
                Margin = new Thickness(0, 1, 0, 1),
                Children = { dataBox, Place(labelBlock, 1, new Thickness(4, 0, 0, 0)) }
            };
            _list.Children.Add(row);
            _rows.Add(new Row(schema, name, dataBox, row, label.ToLowerInvariant()));
        }
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    private void ApplyFilter(string? text)
    {
        var q = (text ?? string.Empty).Trim().ToLowerInvariant();
        var filtering = q.Length > 0;
        foreach (var r in _rows)
        {
            r.Container.IsVisible = !filtering || r.Label.Contains(q, StringComparison.Ordinal);
        }
    }
}
