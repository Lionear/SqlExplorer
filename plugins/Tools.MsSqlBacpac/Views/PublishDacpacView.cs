using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Route-B view for <see cref="PublishDacpacTool"/>: a <c>.dacpac</c> picker with an inline header, a
/// read-only target-database field (the selected node), the deploy toggles, and a warn banner. Seeds the
/// toggle context values on construction so Execute sees the defaults.
/// </summary>
public sealed class PublishDacpacView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly TextBox _fileBox;
    private readonly TextBox _preview;

    public PublishDacpacView(IToolUiContext context)
    {
        _context = context;

        _fileBox = new TextBox { PlaceholderText = "…database.dacpac" };
        _fileBox.TextChanged += (_, _) =>
        {
            _context.SetValue("filePath", _fileBox.Text);
            _ = LoadPreviewAsync(_fileBox.Text);
        };

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickOpenFileAsync("dacpac");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen;
            }
        };

        _preview = MsSqlBacpacUi.PreviewBox();

        var targetBox = MsSqlBacpacUi.ReadonlyField(TargetName());

        var blockDataLoss = Checkbox("Block on possible data loss", "blockDataLoss", check: true, bold: true);
        var singleTx = Checkbox("Run within a single transaction", "singleTransaction", check: true);
        var dropObjects = Checkbox("Drop objects in the target that are not in the DACPAC", "dropObjects", check: false);

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Target database: {TargetName()}", FontWeight = FontWeight.SemiBold, FontSize = 13 },
                new TextBlock { Text = "The live schema will be updated to match the DACPAC.", Opacity = 0.7, FontSize = 11.5 },
                new TextBlock { Text = "DACPAC file", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _fileBox, Place(browse, 1, new Thickness(8, 0, 0, 0)) }
                },
                _preview,
                new TextBlock { Text = "Target database", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                targetBox,
                new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = { blockDataLoss, singleTx, dropObjects } },
                MsSqlBacpacUi.Banner(
                    "Publishing changes the live schema. With \"block on possible data loss\" on, it aborts before any data is lost.",
                    danger: false)
            }
        };
    }

    private CheckBox Checkbox(string label, string key, bool check, bool bold = false)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal },
            IsChecked = check
        };
        box.IsCheckedChanged += (_, _) => _context.SetValue(key, box.IsChecked == true ? "true" : "false");
        _context.SetValue(key, check ? "true" : "false");
        return box;
    }

    private async Task LoadPreviewAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Dispatcher.UIThread.Post(() => _preview.Text = string.Empty);
            return;
        }

        try
        {
            var header = await Task.Run(() => DacFxPreview.Dacpac(path));
            Dispatcher.UIThread.Post(() => _preview.Text = header);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _preview.Text = $"Could not read this file: {ex.Message}");
        }
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    private string TargetName() => _context.Node?.Name ?? _context.Profile.Database ?? _context.Profile.Name;
}
