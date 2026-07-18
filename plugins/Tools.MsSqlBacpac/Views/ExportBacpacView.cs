using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Route-B view for <see cref="ExportBacpacTool"/>: a pre-filled <c>.bacpac</c> destination and a
/// <see cref="TableDataSelectionTree"/>. The schema is always exported in full (a fixed note says so); only
/// each table's <em>data</em> is selectable. The selection is serialized to the context as
/// <c>includeAllData</c> + <c>dataTables</c> on every change.
/// </summary>
public sealed class ExportBacpacView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly TableDataSelectionTree _tree = new();
    private readonly TextBox _fileBox;

    public ExportBacpacView(IToolUiContext context)
    {
        _context = context;
        _tree.Changed += UpdateSelection;

        _fileBox = new TextBox { PlaceholderText = "…database.bacpac" };
        _fileBox.TextChanged += (_, _) => _context.SetValue("filePath", _fileBox.Text);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickSaveFileAsync(SuggestedFileName(), "bacpac");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen;
            }
        };

        _fileBox.Text = DefaultPath(); // pre-fill so export is one click away

        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = DatabaseName(), FontWeight = FontWeight.SemiBold, FontSize = 13.5 },
                new TextBlock { Text = "BACPAC file", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _fileBox, Place(browse, 1, new Thickness(8, 0, 0, 0)) }
                },
                new TextBlock
                {
                    Text = "The schema is always exported in full — only each table's data is selectable below.",
                    Opacity = 0.7, FontSize = 11.5, FontStyle = FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
                },
                _tree.FilterBox
            }
        };

        Grid.SetRow(_tree.TreeHost, 1);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(top);
        root.Children.Add(_tree.TreeHost);
        Content = root;

        _tree.ShowMessage("Loading tables…");
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
            var tables = await ReadTablesAsync();
            Dispatcher.UIThread.Post(() =>
            {
                _tree.SetTables(tables);
                UpdateSelection();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _tree.ShowMessage($"Could not read tables: {ex.Message}"));
        }
    }

    // Three-part names against the target catalog so the list never depends on the connection's own
    // InitialCatalog (which may be master). is_ms_shipped = 0 drops system tables.
    private async Task<IReadOnlyList<(string Schema, string Name)>> ReadTablesAsync()
    {
        var db = DatabaseName().Replace("]", "]]");
        var sql =
            $"SELECT s.name AS SchemaName, t.name AS TableName " +
            $"FROM [{db}].sys.tables t " +
            $"JOIN [{db}].sys.schemas s ON t.schema_id = s.schema_id " +
            $"WHERE t.is_ms_shipped = 0 " +
            $"ORDER BY s.name, t.name";

        QueryResult result = await _context.QueryAsync(sql, CancellationToken.None);
        return result.Rows
            .Select(r => (Schema: Convert.ToString(r[0]) ?? string.Empty, Name: Convert.ToString(r[1]) ?? string.Empty))
            .ToList();
    }

    private void UpdateSelection()
    {
        _context.SetValue("includeAllData", _tree.AllChecked ? "true" : "false");
        _context.SetValue("dataTables",
            string.Join('\n', _tree.CheckedTables.Select(t => $"{t.Schema}\t{t.Name}")));
    }

    private string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SuggestedFileName());

    private string SuggestedFileName()
    {
        var safe = string.Concat(DatabaseName().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{safe}.bacpac";
    }

    private string DatabaseName() => _context.Node?.Name ?? _context.Profile.Database ?? _context.Profile.Name;
}
