using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Lionear.SqlExplorer.Tools.MsSqlAdmin;

/// <summary>
/// Route-B view for <see cref="ShrinkFileTool"/>, modelled on SSMS' Shrink File dialog. Loads every file
/// of the current database once (through <see cref="IToolUiContext.QueryAsync"/>), then cascades File
/// type → Filegroup → File name and shows the selected file's allocated/used space. The three shrink
/// actions map to the DBCC SHRINKFILE forms; "Empty file" is disabled when the file is alone in its
/// filegroup (nowhere to migrate to). Choices flow back through the context for the tool to execute.
/// </summary>
public sealed class ShrinkFileView : UserControl
{
    private sealed record FileRow(string Name, string Type, string Filegroup, decimal AllocMb, decimal UsedMb);

    private readonly IToolUiContext _context;
    private List<FileRow> _files = [];

    private readonly ComboBox _typeBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _filegroupBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _nameBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly TextBlock _allocated = new() { Text = "—" };
    private readonly TextBlock _available = new() { Text = "—" };

    private readonly RadioButton _release;
    private readonly RadioButton _reorganize;
    private readonly RadioButton _empty;
    private readonly NumericUpDown _targetMb;
    private readonly TextBlock _minLabel = new() { Opacity = 0.6, FontSize = 11 };

    public ShrinkFileView(IToolUiContext context)
    {
        _context = context;
        context.SetValue(ShrinkFileTool.ActionKey, ShrinkFileTool.ActionRelease);

        const string group = "shrinkFileAction";
        _release = new RadioButton { GroupName = group, Content = Wrap("Release unused space"), IsChecked = true };
        _reorganize = new RadioButton { GroupName = group, Content = Wrap("Reorganize pages before releasing unused space") };
        _empty = new RadioButton { GroupName = group, Content = Wrap("Empty file by migrating the data to other files in the same filegroup") };

        _targetMb = new NumericUpDown
        {
            Minimum = 0,
            Increment = 1,
            IsEnabled = false,
            FormatString = "0",
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _targetMb.ValueChanged += (_, _) => context.SetValue(ShrinkFileTool.TargetMbKey, ((int)(_targetMb.Value ?? 0)).ToString());

        _release.IsCheckedChanged += (_, _) => OnActionChanged();
        _reorganize.IsCheckedChanged += (_, _) => OnActionChanged();
        _empty.IsCheckedChanged += (_, _) => OnActionChanged();

        _typeBox.SelectionChanged += (_, _) => { RebuildFilegroups(); RebuildNames(); };
        _filegroupBox.SelectionChanged += (_, _) => RebuildNames();
        _nameBox.SelectionChanged += (_, _) => OnFileChanged();

        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "File", FontWeight = FontWeight.SemiBold },
                Labeled("File type", _typeBox),
                Labeled("Filegroup", _filegroupBox),
                Labeled("File name", _nameBox),
                new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.25, Margin = new Thickness(0, 4, 0, 4) },
                Row("Currently allocated space", _allocated),
                Row("Available free space", _available),
                new TextBlock { Text = "Shrink action", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                _release,
                _reorganize,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Shrink file to (MB)", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 },
                        _targetMb,
                        _minLabel
                    }
                },
                _empty
            }
        };

        _ = LoadAsync();
    }

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Control Labeled(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        var name = new TextBlock { Text = label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(name);
        grid.Children.Add(control);
        return grid;
    }

    private static Grid Row(string label, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        var name = new TextBlock { Text = label, Opacity = 0.7 };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }

    private async Task LoadAsync()
    {
        try
        {
            var result = await _context.QueryAsync(
                """
                SELECT mf.name, mf.type_desc, ISNULL(fg.name, ''),
                    CAST(mf.size * 8.0 / 1024 AS decimal(18,2)),
                    CAST(FILEPROPERTY(mf.name, 'SpaceUsed') * 8.0 / 1024 AS decimal(18,2))
                FROM sys.database_files mf
                LEFT JOIN sys.filegroups fg ON mf.data_space_id = fg.data_space_id
                ORDER BY mf.type_desc, mf.name
                """,
                CancellationToken.None);

            var files = result.Rows.Select(r => new FileRow(
                (string)r[0]!,
                DisplayType((string)r[1]!),
                r[2] as string ?? "",
                r[3] as decimal? ?? 0,
                r[4] as decimal? ?? 0)).ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _files = files;
                _typeBox.ItemsSource = files.Select(f => f.Type).Distinct().ToList();
                if (_typeBox.ItemCount > 0)
                {
                    _typeBox.SelectedIndex = 0; // triggers the cascade
                }
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _allocated.Text = $"(unavailable: {ex.Message})");
        }
    }

    // ROWS = a data file, LOG = the transaction log; anything else keeps its raw type_desc.
    private static string DisplayType(string typeDesc) => typeDesc switch
    {
        "ROWS" => "Data",
        "LOG" => "Log",
        _ => typeDesc
    };

    private void RebuildFilegroups()
    {
        var type = _typeBox.SelectedItem as string;
        var isData = type == "Data";
        _filegroupBox.IsEnabled = isData;
        var groups = isData
            ? _files.Where(f => f.Type == type && f.Filegroup.Length > 0).Select(f => f.Filegroup).Distinct().ToList()
            : [];
        _filegroupBox.ItemsSource = groups;
        _filegroupBox.SelectedIndex = groups.Count > 0 ? 0 : -1;
    }

    private void RebuildNames()
    {
        var names = SelectedFiles().Select(f => f.Name).ToList();
        _nameBox.ItemsSource = names;
        _nameBox.SelectedIndex = names.Count > 0 ? 0 : -1;
        if (names.Count == 0)
        {
            OnFileChanged();
        }
    }

    private IEnumerable<FileRow> SelectedFiles()
    {
        var type = _typeBox.SelectedItem as string;
        var fg = _filegroupBox.SelectedItem as string;
        return _files.Where(f => f.Type == type && (type != "Data" || fg is null || f.Filegroup == fg));
    }

    private void OnFileChanged()
    {
        var file = _files.FirstOrDefault(f => f.Name == _nameBox.SelectedItem as string);
        if (file is null)
        {
            _allocated.Text = "—";
            _available.Text = "—";
            _context.SetValue(ShrinkFileTool.LogicalNameKey, null);
            return;
        }

        _context.SetValue(ShrinkFileTool.LogicalNameKey, file.Name);
        _allocated.Text = $"{file.AllocMb:N2} MB";
        var freeMb = file.AllocMb - file.UsedMb;
        var pct = file.AllocMb > 0 ? (double)(freeMb / file.AllocMb) * 100 : 0;
        _available.Text = $"{freeMb:N2} MB ({pct:N0}%)";

        // "Shrink file to" cannot go below the space already used; seed the value at that minimum.
        var minMb = (int)Math.Ceiling(file.UsedMb);
        _targetMb.Minimum = minMb;
        _targetMb.Value = minMb;
        _minLabel.Text = $"(Minimum is {minMb} MB)";
        _context.SetValue(ShrinkFileTool.TargetMbKey, minMb.ToString());

        // Empty-file only makes sense with another file in the same filegroup to migrate into.
        var siblings = _files.Count(f => f.Type == file.Type && f.Filegroup == file.Filegroup);
        var canEmpty = siblings > 1;
        _empty.IsEnabled = canEmpty;
        ToolTip.SetTip(_empty, canEmpty ? null : "Needs another file in the same filegroup to migrate the data into.");
        if (!canEmpty && _empty.IsChecked == true)
        {
            _release.IsChecked = true;
        }
    }

    private void OnActionChanged()
    {
        var action = _empty.IsChecked == true ? ShrinkFileTool.ActionEmpty
            : _reorganize.IsChecked == true ? ShrinkFileTool.ActionReorganize
            : ShrinkFileTool.ActionRelease;
        _context.SetValue(ShrinkFileTool.ActionKey, action);
        _targetMb.IsEnabled = action == ShrinkFileTool.ActionReorganize;
    }
}
