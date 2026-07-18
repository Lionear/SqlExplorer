using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Route-B view for <see cref="ExtractDacpacTool"/>: a <c>.dacpac</c> destination, an application name +
/// version, and a few <c>DacExtractOptions</c> toggles. Schema-only — a note says so. Seeds every context
/// value on construction so Execute sees the defaults even if the user changes nothing.
/// </summary>
public sealed class ExtractDacpacView : UserControl
{
    private readonly IToolUiContext _context;
    private readonly TextBox _fileBox;

    public ExtractDacpacView(IToolUiContext context)
    {
        _context = context;

        _fileBox = new TextBox { PlaceholderText = "…database.dacpac", Text = DefaultPath() };
        _fileBox.TextChanged += (_, _) => _context.SetValue("filePath", _fileBox.Text);
        _context.SetValue("filePath", _fileBox.Text);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var chosen = await _context.PickSaveFileAsync(SuggestedFileName(), "dacpac");
            if (!string.IsNullOrEmpty(chosen))
            {
                _fileBox.Text = chosen;
            }
        };

        var appNameBox = new TextBox { Text = DatabaseName() };
        appNameBox.TextChanged += (_, _) => _context.SetValue("appName", appNameBox.Text);
        _context.SetValue("appName", appNameBox.Text);

        var versionBox = new TextBox { Text = "1.0.0.0", Width = 120 };
        versionBox.TextChanged += (_, _) => _context.SetValue("version", versionBox.Text);
        _context.SetValue("version", versionBox.Text);

        var appScoped = Checkbox("Extract application-scoped objects only", "appScopedOnly", check: true);
        var verify = Checkbox("Verify referential integrity of the extracted schema", "verifyExtraction", check: false);
        var permissions = Checkbox("Include permissions", "includePermissions", check: false);

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = DatabaseName(), FontWeight = FontWeight.SemiBold, FontSize = 13.5 },
                new TextBlock { Text = "Schema-only — no data is written to a DACPAC.", Opacity = 0.7, FontSize = 11.5 },
                new TextBlock { Text = "DACPAC file", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children = { _fileBox, Place(browse, 1, new Thickness(8, 0, 0, 0)) }
                },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Thickness(0, 2, 0, 0),
                    Children =
                    {
                        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Application name", Opacity = 0.7, FontSize = 11.5 }, appNameBox } },
                        Place(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Version", Opacity = 0.7, FontSize = 11.5 }, versionBox } }, 1, new Thickness(12, 0, 0, 0))
                    }
                },
                new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = { appScoped, verify, permissions } },
                new TextBlock
                {
                    Text = "A DACPAC contains schema only — handy for CI/CD and schema comparison.",
                    Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                }
            }
        };
    }

    private CheckBox Checkbox(string label, string key, bool check)
    {
        var box = new CheckBox { Content = label, IsChecked = check };
        box.IsCheckedChanged += (_, _) => _context.SetValue(key, box.IsChecked == true ? "true" : "false");
        _context.SetValue(key, check ? "true" : "false");
        return box;
    }

    private static Control Place(Control c, int column, Thickness margin)
    {
        Grid.SetColumn(c, column);
        c.Margin = margin;
        return c;
    }

    private string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SuggestedFileName());

    private string SuggestedFileName()
    {
        var safe = string.Concat(DatabaseName().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{safe}.dacpac";
    }

    private string DatabaseName() => _context.Node?.Name ?? _context.Profile.Database ?? _context.Profile.Name;
}
