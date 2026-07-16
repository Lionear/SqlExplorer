using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>One row's spec for <see cref="SelectableObjectTree"/>. <see cref="DataAllowed"/> shows/hides the
/// Data checkbox's meaning for the row's kind (only tables have a data half); <see cref="DataChecked"/>/
/// <see cref="DataEnabled"/> let the caller reflect real constraints (Backup: always allowed for tables;
/// Restore: only enabled when the backup actually captured data for that table).</summary>
public sealed record TreeItemSpec(string Kind, string Schema, string Name, bool DataAllowed, bool DataChecked = true, bool DataEnabled = true);

/// <summary>
/// The two-checkbox (<c>[Schema][Data]</c>) object-selection tree shared by <see cref="BackupSelectionView"/>
/// and <see cref="RestoreSelectionView"/>: collapsible groups with a select-all header, a filter box, and
/// per-row checkboxes. Both views end up producing the same <see cref="SelectionEntry"/> shape, so the
/// widget owns that too — extracted after the second view needed the identical rendering/scroll/filter
/// logic (and the identical scroll-clipping bug fix), to avoid the two trees drifting apart.
/// </summary>
public sealed class SelectableObjectTree
{
    private sealed record RowState(string Kind, string Schema, string Name, CheckBox SchemaBox, CheckBox DataBox);
    private sealed record RowUi(Control Row, string Label);

    private sealed class GroupUi
    {
        public required Control Header { get; init; }
        public required StackPanel Body { get; init; }
        public required List<RowUi> Rows { get; init; }
        public required Action<bool> SetExpanded { get; init; }
    }

    // Two fixed-width checkbox columns so the header labels and every row's checkboxes line up.
    private const string RowColumns = "58,52,*";

    private readonly List<RowState> _rows = [];
    private readonly List<GroupUi> _groups = [];
    private readonly StackPanel _tree = new() { Spacing = 2 };

    public TextBox FilterBox { get; } = new() { PlaceholderText = "Filter objects…" };

    public Control TreeHost { get; }

    /// <summary>Raised after any checkbox changes, so the caller can push <see cref="Selection"/> into its
    /// <c>IToolUiContext</c>.</summary>
    public event Action? Changed;

    public SelectableObjectTree()
    {
        FilterBox.TextChanged += (_, _) => ApplyFilter(FilterBox.Text);
        TreeHost = ObjectTreeScaffold.BuildScrollHost(_tree);
        _tree.Children.Add(new TextBlock { Text = "Loading…", Opacity = 0.6 });
    }

    /// <summary>Rebuild the tree from scratch (e.g. once schema/meta finishes loading, or a different file
    /// was chosen). Groups with no items are omitted entirely.</summary>
    public void SetGroups(IEnumerable<(string Title, IReadOnlyList<TreeItemSpec> Items)> groups)
    {
        _tree.Children.Clear();
        _rows.Clear();
        _groups.Clear();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(RowColumns), Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(ColumnLabel("Schema", 0));
        header.Children.Add(ColumnLabel("Data", 1));
        header.Children.Add(Place(new TextBlock { Text = "Object", FontWeight = FontWeight.SemiBold, Opacity = 0.65, FontSize = 11.5 }, 2, new Thickness(4, 0, 0, 0)));
        _tree.Children.Add(header);

        foreach (var (title, items) in groups)
        {
            AddGroup(title, items);
        }

        if (_rows.Count == 0)
        {
            _tree.Children.Add(new TextBlock { Text = "No objects found.", Opacity = 0.6 });
        }
    }

    /// <summary>Replace the tree with a plain informational message (e.g. "choose a file", "legacy backup —
    /// nothing to pick"). Clears any previous selection state.</summary>
    public void ShowMessage(string text)
    {
        _tree.Children.Clear();
        _rows.Clear();
        _groups.Clear();
        _tree.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
    }

    /// <summary>Append a non-interactive, greyed-out row after the groups (e.g. "Indexes — not supported")
    /// — purely informational, takes no part in <see cref="Selection"/>. Call after <see cref="SetGroups"/>.</summary>
    public void AppendDisabledRow(string text)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(RowColumns),
            Margin = new Thickness(0, 10, 0, 2),
            IsEnabled = false,
            Opacity = 0.5,
            Children =
            {
                new CheckBox { IsChecked = false, IsEnabled = false, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center },
                Place(new TextBlock { Text = text, FontStyle = FontStyle.Italic, FontSize = 11.5 }, 2, new Thickness(4, 0, 0, 0))
            }
        };
        _tree.Children.Add(row);
    }

    public IReadOnlyList<SelectionEntry> Selection =>
        _rows.Select(r => new SelectionEntry(r.Kind, r.Schema, r.Name, r.SchemaBox.IsChecked == true, r.DataBox.IsChecked == true)).ToList();

    private static Control Place(Control c, int column, Thickness? margin = null)
    {
        Grid.SetColumn(c, column);
        if (margin is { } m) c.Margin = m;
        return c;
    }

    private static Control ColumnLabel(string text, int column) =>
        Place(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.65, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center }, column);

    private void AddGroup(string title, IReadOnlyList<TreeItemSpec> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var body = new StackPanel { Spacing = 0 };
        var groupRows = new List<RowState>();
        var rowUis = new List<RowUi>();
        var groupHasData = items.Any(i => i.DataAllowed); // uniform within a group in practice (tables vs objects)

        foreach (var item in items)
        {
            var schemaBox = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
            var dataBox = new CheckBox
            {
                IsChecked = item.DataAllowed && item.DataChecked,
                IsEnabled = item.DataAllowed && item.DataEnabled,
                MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center
            };

            // Data on ⇒ Schema on (you can't act on rows without the structure) — only for data-capable rows.
            if (item.DataAllowed)
            {
                dataBox.IsCheckedChanged += (_, _) =>
                {
                    if (dataBox.IsChecked == true)
                    {
                        schemaBox.IsChecked = true;
                    }
                };
            }

            schemaBox.IsCheckedChanged += (_, _) => Changed?.Invoke();
            dataBox.IsCheckedChanged += (_, _) => Changed?.Invoke();

            var label = string.IsNullOrEmpty(item.Schema) ? item.Name : $"{item.Schema}.{item.Name}";
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
                Children = { schemaBox, Place(dataBox, 1), Place(labelBlock, 2, new Thickness(4, 0, 0, 0)) }
            };

            body.Children.Add(row);
            var state = new RowState(item.Kind, item.Schema, item.Name, schemaBox, dataBox);
            groupRows.Add(state);
            _rows.Add(state);
            rowUis.Add(new RowUi(row, label.ToLowerInvariant()));
        }

        var (headerControl, chevron) = BuildGroupHeader(title, items.Count, groupHasData, body, groupRows);
        _groups.Add(new GroupUi
        {
            Header = headerControl,
            Body = body,
            Rows = rowUis,
            SetExpanded = expanded =>
            {
                body.IsVisible = expanded;
                chevron.Text = expanded ? "▾" : "▸";
            }
        });

        _tree.Children.Add(headerControl);
        _tree.Children.Add(body);
    }

    // A group header row: [select-all schema] [select-all data?] [▸/▾ Title (count)]. Clicking the title
    // collapses/expands the group; the checkboxes toggle every row in the group at once.
    private (Control Header, TextBlock Chevron) BuildGroupHeader(string title, int count, bool groupHasData, StackPanel body, List<RowState> groupRows)
    {
        var chevron = new TextBlock { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.7 };
        var titleBlock = new TextBlock { Text = $"{title} ({count})", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, titleBlock } }
        };
        toggle.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = body.IsVisible ? "▾" : "▸";
        };

        var allSchema = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        allSchema.IsCheckedChanged += (_, _) =>
        {
            if (allSchema.IsChecked is { } v)
            {
                foreach (var r in groupRows) r.SchemaBox.IsChecked = v;
            }
        };

        var allData = new CheckBox { IsChecked = groupHasData, IsEnabled = groupHasData, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        if (groupHasData)
        {
            allData.IsCheckedChanged += (_, _) =>
            {
                if (allData.IsChecked is { } v)
                {
                    foreach (var r in groupRows.Where(r => r.DataBox.IsEnabled)) r.DataBox.IsChecked = v;
                }
            };
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(RowColumns),
            Margin = new Thickness(0, 10, 0, 2),
            Children = { allSchema, Place(allData, 1), Place(toggle, 2, new Thickness(2, 0, 0, 0)) }
        };
        return (grid, chevron);
    }

    // Narrow the object list to rows whose "schema.name" contains the query. Matching groups are force-
    // expanded so hits are visible; groups with no match are hidden. An empty query restores the default
    // (all rows shown, groups expanded).
    private void ApplyFilter(string? text)
    {
        var q = (text ?? string.Empty).Trim().ToLowerInvariant();
        var filtering = q.Length > 0;

        foreach (var g in _groups)
        {
            var matches = 0;
            foreach (var r in g.Rows)
            {
                var visible = !filtering || r.Label.Contains(q, StringComparison.Ordinal);
                r.Row.IsVisible = visible;
                if (visible) matches++;
            }

            g.Header.IsVisible = !filtering || matches > 0;
            g.SetExpanded(!filtering || matches > 0);
        }
    }
}
