using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Route-B view for <see cref="UniversalBackupTool"/>: a pre-filled destination path + optional passphrase,
/// and a <see cref="SelectableObjectTree"/> grouped by type (Tables / Views / Procedures / Functions /
/// Triggers) with two independent checkboxes per row — <c>[Schema] [Data]</c> (Data enabled for tables
/// only). The selection is serialized to the tool context under "selection" on every change;
/// <c>ExecuteAsync</c> filters on it.
/// </summary>
public sealed class BackupSelectionView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly SelectableObjectTree _tree = new();
    private readonly TextBox _fileBox;

    public BackupSelectionView(IToolUiContext context)
    {
        _context = context;
        _tree.Changed += UpdateSelection;

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
                _tree.FilterBox
            }
        };

        // The object list fills the remaining height (row "*"), so it grows with the window and scrolls
        // internally instead of being a fixed box with dead space below.
        Grid.SetRow(_tree.TreeHost, 1);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(top);
        root.Children.Add(_tree.TreeHost);
        Content = root;

        _tree.ShowMessage("Loading schema…");
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
            Dispatcher.UIThread.Post(() => _tree.ShowMessage($"Could not read schema: {ex.Message}"));
        }
    }

    private void BuildTree(IReadOnlyList<TableRef> tables, IReadOnlyList<SchemaReader.BackupObjectRef> objects)
    {
        _tree.SetGroups(
        [
            ("Tables", tables.Select(t => new TreeItemSpec(BackupSelection.TableKind, t.Schema ?? string.Empty, t.Table, DataAllowed: true)).ToList()),
            ("Views", ObjectsOf(objects, LbakObjectKind.View)),
            ("Procedures", ObjectsOf(objects, LbakObjectKind.Procedure)),
            ("Functions", ObjectsOf(objects, LbakObjectKind.Function)),
            ("Triggers", ObjectsOf(objects, LbakObjectKind.Trigger))
        ]);
        UpdateSelection();
    }

    private static IReadOnlyList<TreeItemSpec> ObjectsOf(IReadOnlyList<SchemaReader.BackupObjectRef> objects, LbakObjectKind kind) =>
        objects.Where(o => o.Kind == kind)
            .Select(o => new TreeItemSpec(kind.ToString().ToLowerInvariant(), o.Schema, o.Name, DataAllowed: false))
            .ToList();

    private void UpdateSelection() => _context.SetValue("selection", BackupSelection.Serialize(_tree.Selection));

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
