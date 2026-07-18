using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Data.SqlClient;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Route-B view for <see cref="ImportBacpacTool"/>: a <c>.bacpac</c> picker, an inline header (read from the
/// file the moment it's chosen), a target-database name field (pre-filled from the file name), and a
/// destructive banner. Sets <c>filePath</c> and <c>targetDatabase</c> on the context.
/// </summary>
public sealed class ImportBacpacView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly TextBox _fileBox;
    private readonly TextBox _nameBox;
    private readonly TextBox _preview;
    private bool _nameEdited;

    public ImportBacpacView(IToolUiContext context)
    {
        _context = context;

        _fileBox = new TextBox { PlaceholderText = "…database.bacpac" };
        _nameBox = new TextBox { PlaceholderText = "New database name" };
        _nameBox.TextChanged += (_, _) =>
        {
            _nameEdited = true;
            _context.SetValue("targetDatabase", _nameBox.Text);
        };

        _fileBox.TextChanged += (_, _) =>
        {
            _context.SetValue("filePath", _fileBox.Text);
            OnFileChanged(_fileBox.Text);
        };

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickOpenFileAsync("bacpac");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen; // TextChanged picks it up
            }
        };

        _preview = MsSqlBacpacUi.PreviewBox();

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Target server: {ServerName()}", FontWeight = FontWeight.SemiBold, FontSize = 13 },
                new TextBlock { Text = "Creates a new database — it must not already exist.", Opacity = 0.7, FontSize = 11.5 },
                new TextBlock { Text = "BACPAC file", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _fileBox, Place(browse, 1, new Thickness(8, 0, 0, 0)) }
                },
                _preview,
                new TextBlock { Text = "New database name", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                _nameBox,
                MsSqlBacpacUi.Banner(
                    "A new database with this name is created. If it already exists the import fails — existing data is never overwritten.",
                    danger: true)
            }
        };
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    private void OnFileChanged(string? path)
    {
        // Suggest "<file>_import" as the DB name until the user types their own.
        if (!_nameEdited && !string.IsNullOrWhiteSpace(path))
        {
            var suggestion = $"{Path.GetFileNameWithoutExtension(path)}_import";
            _nameBox.Text = suggestion;
            _nameEdited = false; // the programmatic set above flipped it; keep tracking as "auto"
        }

        _ = LoadPreviewAsync(path);
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
            var header = await Task.Run(() => DacFxPreview.Bacpac(path));
            Dispatcher.UIThread.Post(() => _preview.Text = header);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _preview.Text = $"Could not read this file: {ex.Message}");
        }
    }

    private string ServerName()
    {
        try
        {
            return new SqlConnectionStringBuilder(_context.Profile.ConnectionString).DataSource;
        }
        catch
        {
            return _context.Profile.Name;
        }
    }
}
