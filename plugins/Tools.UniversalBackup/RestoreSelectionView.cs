using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Route-B view for <see cref="UniversalRestoreTool"/>: a backup-file picker + optional passphrase, a
/// "drop &amp; recreate" toggle for tables that already exist, and — once a file is chosen — a
/// <see cref="SelectableObjectTree"/> read from the backup's always-plaintext <see cref="LbakMeta"/> (no
/// passphrase needed to see it). Tables get the same <c>[Schema][Data]</c> pair as the Backup dialog — Data
/// is only enabled when the backup actually captured that table's rows (<see cref="BackupTableMeta.HasData"/>),
/// so it's not possible to "check" a data restore the file can't deliver. Non-table objects get a single
/// effective checkbox (Data column always unchecked/disabled), same as Backup's non-table groups. A backup
/// written before this feature existed (<c>Tables</c>/<c>Objects</c> both null) shows an explanatory line
/// instead of a tree; the selection then stays unset, which restores everything — identical to the tool's
/// pre-2b behaviour.
/// </summary>
public sealed class RestoreSelectionView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly SelectableObjectTree _tree = new();
    private readonly TextBox _fileBox;
    private readonly TextBlock _infoText;

    public RestoreSelectionView(IToolUiContext context)
    {
        _context = context;
        _tree.Changed += UpdateSelection;

        _fileBox = new TextBox { PlaceholderText = "…backup.lbak" };
        _fileBox.TextChanged += (_, _) =>
        {
            _context.SetValue("filePath", _fileBox.Text);
            _ = LoadMetaAsync(_fileBox.Text);
        };

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickOpenFileAsync("lbak");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen; // TextChanged above picks this up and reloads the tree
            }
        };

        var passBox = new TextBox { PasswordChar = '●', PlaceholderText = "Passphrase (only if the backup is encrypted)" };
        passBox.TextChanged += (_, _) => _context.SetValue("passphrase", passBox.Text);

        var dropRecreateBox = new CheckBox { Content = "Drop & recreate tables that already exist" };
        dropRecreateBox.IsCheckedChanged += (_, _) =>
            _context.SetValue("dropRecreate", dropRecreateBox.IsChecked == true ? "true" : "false");

        _infoText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, IsVisible = false };

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
                new TextBlock { Text = "Passphrase (only if the backup is encrypted)", Opacity = 0.75, FontSize = 12 },
                passBox,
                dropRecreateBox,
                _infoText,
                new TextBlock { Text = "Objects to restore", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                _tree.FilterBox
            }
        };

        Grid.SetRow(_tree.TreeHost, 1);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(top);
        root.Children.Add(_tree.TreeHost);
        Content = root;

        _tree.ShowMessage("Choose a backup file to see what it contains.");
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    // Reading the header never needs a passphrase (it's always plaintext) — the tree can populate the
    // moment a file is chosen, before Execute is even enabled.
    private async Task LoadMetaAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var meta = await Task.Run(() => LbakFormat.ReadMeta(path));
            Dispatcher.UIThread.Post(() => BuildTree(meta));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _infoText.IsVisible = false;
                _tree.ShowMessage($"Could not read this file: {ex.Message}");
            });
        }
    }

    private void BuildTree(LbakMeta meta)
    {
        // No object list at all → this backup predates Fase 2b. Leave the selection unset (BackupSelection
        // treats null as "restore everything"), same as before this feature existed.
        if (meta.Tables is null && meta.Objects is null)
        {
            _infoText.Text = "This backup was made before per-object selection existed — everything in it will be restored.";
            _infoText.IsVisible = true;
            _tree.ShowMessage($"{meta.TableCount} table(s), created {meta.CreatedUtc}.");
            _context.SetValue("selection", null);
            return;
        }

        _infoText.IsVisible = false;

        _tree.SetGroups(
        [
            ("Tables", (meta.Tables ?? []).Select(t =>
                new TreeItemSpec(BackupSelection.TableKind, t.Schema, t.Name, DataAllowed: true, DataChecked: t.HasData, DataEnabled: t.HasData)).ToList()),
            ("Views", ObjectsOf(meta.Objects, LbakObjectKind.View)),
            ("Procedures", ObjectsOf(meta.Objects, LbakObjectKind.Procedure)),
            ("Functions", ObjectsOf(meta.Objects, LbakObjectKind.Function)),
            ("Triggers", ObjectsOf(meta.Objects, LbakObjectKind.Trigger))
        ]);
        _tree.AppendDisabledRow("Indexes — not captured in the backup, recreate manually");

        UpdateSelection();
    }

    private static IReadOnlyList<TreeItemSpec> ObjectsOf(IReadOnlyList<BackupObjectMeta>? objects, LbakObjectKind kind) =>
        (objects ?? []).Where(o => o.Kind == kind)
            .Select(o => new TreeItemSpec(kind.ToString().ToLowerInvariant(), o.Schema, o.Name, DataAllowed: false))
            .ToList();

    private void UpdateSelection() => _context.SetValue("selection", BackupSelection.Serialize(_tree.Selection));
}
