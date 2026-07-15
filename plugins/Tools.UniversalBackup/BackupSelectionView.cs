using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Route-B view for <see cref="UniversalBackupTool"/>: a pre-filled destination path + optional passphrase,
/// and an object tree grouped by type (Tables / Views / Procedures / Functions / Triggers) with two
/// independent checkboxes per row — <c>[Schema] [Data]</c> (Data enabled for tables only). The selection is
/// serialized to the tool context under "selection" on every change; <c>ExecuteAsync</c> filters on it.
/// </summary>
public sealed class BackupSelectionView : UserControl
{
    private sealed record RowState(string Kind, string Schema, string Name, CheckBox SchemaBox, CheckBox DataBox);

    // One rendered object row plus its lowercased "schema.name" for filtering.
    private sealed record RowUi(Control Row, string Label);

    // A rendered group (Tables/Views/…): its header, collapsible body, rows and how to expand/collapse it.
    private sealed class GroupUi
    {
        public required Control Header { get; init; }
        public required StackPanel Body { get; init; }
        public required List<RowUi> Rows { get; init; }
        public required bool CollapsedByDefault { get; init; }
        public required Action<bool> SetExpanded { get; init; }
    }

    private readonly IToolUiContext _context;
    private readonly List<RowState> _rows = [];
    private readonly List<GroupUi> _groups = [];
    private readonly StackPanel _tree = new() { Spacing = 2 };
    private readonly TextBox _fileBox;
    private readonly TextBox _filterBox;

    public BackupSelectionView(IToolUiContext context)
    {
        _context = context;

        _fileBox = new TextBox { PlaceholderText = "…backup.lbak" };
        _fileBox.TextChanged += (_, _) => _context.SetValue("filePath", _fileBox.Text);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickSaveFileAsync(SuggestedFileName(), "lbak");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen;
            }
        };

        var passBox = new TextBox { PasswordChar = '●', PlaceholderText = "Passphrase (optional)" };
        passBox.TextChanged += (_, _) => _context.SetValue("passphrase", passBox.Text);

        _fileBox.Text = DefaultPath(); // pre-fill so the user can back up straight away

        // Filter box: type to narrow the object list across every group, so a specific procedure isn't
        // buried under a large table list. Empty = show everything (groups back to their default state).
        _filterBox = new TextBox { PlaceholderText = "Filter objects…" };
        _filterBox.TextChanged += (_, _) => ApplyFilter(_filterBox.Text);

        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Backup file", FontWeight = FontWeight.SemiBold },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _fileBox, Place(browse, 1, new Thickness(8, 0, 0, 0)) }
                },
                new TextBlock { Text = "Passphrase (optional — leave empty for an unencrypted backup)", Opacity = 0.75, FontSize = 12 },
                passBox,
                new TextBlock { Text = "Objects to include", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                _filterBox
            }
        };

        // The object list fills the remaining height (row "*"), so it grows with the window and scrolls
        // internally instead of being a fixed box with dead space below.
        var listBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            CornerRadius = new CornerRadius(4),
            MinHeight = 120,
            Margin = new Thickness(0, 4, 0, 0),
            Child = new ScrollViewer
            {
                Content = _tree,
                Padding = new Thickness(8, 8, 8, 12),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        Grid.SetRow(listBorder, 1);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(top);
        root.Children.Add(listBorder);
        Content = root;

        _tree.Children.Add(new TextBlock { Text = "Loading schema…", Opacity = 0.6 });
        _ = LoadAsync();
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    private async Task LoadAsync()
    {
        try
        {
            var tables = await SchemaReader.CollectTablesAsync(_context.Provider, _context.Profile, _context.Node, CancellationToken.None);
            var objects = await SchemaReader.CollectObjectRefsAsync(_context.Provider, _context.Profile, _context.Node, CancellationToken.None);
            Dispatcher.UIThread.Post(() => BuildTree(tables, objects));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _tree.Children.Clear();
                _tree.Children.Add(new TextBlock { Text = $"Could not read schema: {ex.Message}", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            });
        }
    }

    // Two fixed-width checkbox columns so the header labels and every row's checkboxes line up.
    private const string RowColumns = "58,52,*";

    private void BuildTree(IReadOnlyList<TableRef> tables, IReadOnlyList<SchemaReader.BackupObjectRef> objects)
    {
        _tree.Children.Clear();
        _rows.Clear();
        _groups.Clear();

        // Column header aligned with the checkbox columns below.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(RowColumns), Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(ColumnLabel("Schema", 0));
        header.Children.Add(ColumnLabel("Data", 1));
        header.Children.Add(Place(new TextBlock { Text = "Object", FontWeight = FontWeight.SemiBold, Opacity = 0.65, FontSize = 11.5 }, 2, new Thickness(4, 0, 0, 0)));
        _tree.Children.Add(header);

        AddGroup("Tables", tables.Select(t => (BackupSelection.TableKind, t.Schema ?? string.Empty, t.Table, /*dataAllowed*/ true)));
        AddGroup("Views", ObjectsOf(objects, LbakObjectKind.View));
        AddGroup("Procedures", ObjectsOf(objects, LbakObjectKind.Procedure));
        AddGroup("Functions", ObjectsOf(objects, LbakObjectKind.Function));
        AddGroup("Triggers", ObjectsOf(objects, LbakObjectKind.Trigger));

        if (_rows.Count == 0)
        {
            _tree.Children.Add(new TextBlock { Text = "No objects found.", Opacity = 0.6 });
        }

        UpdateSelection();
    }

    private static Control ColumnLabel(string text, int column)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.65,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private static IEnumerable<(string Kind, string Schema, string Name, bool DataAllowed)> ObjectsOf(
        IReadOnlyList<SchemaReader.BackupObjectRef> objects, LbakObjectKind kind) =>
        objects.Where(o => o.Kind == kind).Select(o => (kind.ToString().ToLowerInvariant(), o.Schema, o.Name, false));

    private void AddGroup(string title, IEnumerable<(string Kind, string Schema, string Name, bool DataAllowed)> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        // The group's rows live in their own panel so the header can collapse them as a unit.
        var body = new StackPanel { Spacing = 0 };
        var groupRows = new List<RowState>();
        var rowUis = new List<RowUi>();
        var dataAllowed = list[0].DataAllowed; // uniform within a group (tables vs objects)

        foreach (var item in list)
        {
            var schemaBox = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
            var dataBox = new CheckBox { IsChecked = item.DataAllowed, IsEnabled = item.DataAllowed, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };

            // Data on ⇒ Schema on (you can't read rows without knowing the structure) — only for tables.
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

            schemaBox.IsCheckedChanged += (_, _) => UpdateSelection();
            dataBox.IsCheckedChanged += (_, _) => UpdateSelection();

            var label = string.IsNullOrEmpty(item.Schema) ? item.Name : $"{item.Schema}.{item.Name}";
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(RowColumns),
                Margin = new Thickness(0, 1, 0, 1),
                Children =
                {
                    schemaBox,
                    Place(dataBox, 1, new Thickness(0)),
                    Place(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 }, 2, new Thickness(4, 0, 0, 0))
                }
            };

            body.Children.Add(row);
            var state = new RowState(item.Kind, item.Schema, item.Name, schemaBox, dataBox);
            groupRows.Add(state);
            _rows.Add(state);
            rowUis.Add(new RowUi(row, label.ToLowerInvariant()));
        }

        var (headerControl, chevron) = BuildGroupHeader(title, list.Count, dataAllowed, body, groupRows);

        _groups.Add(new GroupUi
        {
            Header = headerControl,
            Body = body,
            Rows = rowUis,
            CollapsedByDefault = false, // start expanded → full, scrollable list; the filter handles findability
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
    // collapses/expands the group; the checkboxes toggle every row in the group at once. Returns the header
    // control and its chevron (so the filter can drive expand/collapse too).
    private (Control Header, TextBlock Chevron) BuildGroupHeader(string title, int count, bool dataAllowed, StackPanel body, List<RowState> groupRows)
    {
        body.IsVisible = true; // groups start expanded; the user can collapse a group via its header

        var chevron = new TextBlock
        {
            Text = "▾", // ▾ expanded / ▸ collapsed
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Opacity = 0.7
        };

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

        // Select-all boxes toggle every row's schema (and data, for tables) in the group.
        var allSchema = new CheckBox { IsChecked = true, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        allSchema.IsCheckedChanged += (_, _) =>
        {
            if (allSchema.IsChecked is { } v)
            {
                foreach (var r in groupRows) r.SchemaBox.IsChecked = v;
            }
        };

        var allData = new CheckBox { IsChecked = dataAllowed, IsEnabled = dataAllowed, MinWidth = 0, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        if (dataAllowed)
        {
            allData.IsCheckedChanged += (_, _) =>
            {
                if (allData.IsChecked is { } v)
                {
                    foreach (var r in groupRows) r.DataBox.IsChecked = v;
                }
            };
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(RowColumns),
            Margin = new Thickness(0, 10, 0, 2),
            Children = { allSchema, Place(allData, 1, new Thickness(0)), Place(toggle, 2, new Thickness(2, 0, 0, 0)) }
        };
        return (grid, chevron);
    }

    // Narrow the object list to rows whose "schema.name" contains the query. Matching groups are force-
    // expanded so hits are visible; groups with no match are hidden. An empty query restores the default
    // (large groups collapsed, all rows shown).
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

            if (filtering)
            {
                g.Header.IsVisible = matches > 0;
                g.SetExpanded(matches > 0);
            }
            else
            {
                g.Header.IsVisible = true;
                g.SetExpanded(!g.CollapsedByDefault);
            }
        }
    }

    private void UpdateSelection()
    {
        var entries = _rows
            .Select(r => new SelectionEntry(r.Kind, r.Schema, r.Name, r.SchemaBox.IsChecked == true, r.DataBox.IsChecked == true))
            .ToList();
        _context.SetValue("selection", BackupSelection.Serialize(entries));
    }

    // "<default folder or home>/<database>.lbak" — always something, so no hard "no file chosen" error.
    private string DefaultPath()
    {
        var folder = (_context as SqlExplorer.Sdk.Tools.IToolHost)?.GetPluginSetting("defaultFolder");
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(folder, SuggestedFileName());
    }

    private string SuggestedFileName()
    {
        var name = _context.Profile.Database ?? _context.Node?.Name ?? _context.Profile.Name;
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{safe}.lbak";
    }
}
