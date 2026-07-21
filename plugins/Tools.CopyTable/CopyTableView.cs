using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Copy Table's own dialog view (Route B): a From → To header, a destination connection + database picker,
/// segmented "what to copy" and row scope, fidelity switches, and two mode cards (Run the copy / Open as
/// script). It only builds the input area — the host frames it with the Run/Cancel buttons, the progress
/// checklist and the success banner. Values are written back through <see cref="IToolUiContext"/> under the
/// same <c>ToolField.Key</c>s <see cref="CopyTableTool"/> reads. Segments/cards are code-built from Borders
/// (not Fluent radios) for full styling control, using DynamicResource theme brushes so it follows the app's
/// light/dark theme.
/// </summary>
internal sealed class CopyTableView : UserControl
{
    private readonly TextBlock _targetChip;
    private readonly ComboBox _databaseBox;

    // Accent tint for a selected card — a low-alpha wash of the accent that reads on both light and dark.
    private static readonly IBrush AccentWash = new SolidColorBrush(Color.Parse("#223574F0"));

    public CopyTableView(IToolUiContext ctx, string initialMode, string sourceTable)
    {
        // Seed the values the host will collect, so a straight-through "Run" uses the shown defaults.
        ctx.SetValue("what", What.Both);
        ctx.SetValue("rows", "All");
        ctx.SetValue("keepIdentity", "true");
        ctx.SetValue("dropExisting", "false");
        ctx.SetValue("mode", initialMode);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 6), Spacing = 18 };

        // ── From → To header ──────────────────────────────────────────────────────────────────────────
        _targetChip = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, FontFamily = Mono, FontSize = 12.5 };
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Chip("FROM", new TextBlock { Text = sourceTable, FontFamily = Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }),
                new TextBlock { Text = "→", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 },
                Chip("TO", _targetChip)
            }
        });

        // ── Destination connection + database ─────────────────────────────────────────────────────────
        var connectionBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ctx.ListConnections(),
            PlaceholderText = "Pick a connection…",
            // Bind (not a captured value): the closed selection box rebuilds the template and a captured value
            // renders blank there — a binding to Name resolves for both the dropdown row and the selection box.
            ItemTemplate = new FuncDataTemplate<ToolConnectionInfo>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ToolConnectionInfo.Name)) }, supportsRecycling: false)
        };
        _databaseBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Pick a database…", IsEnabled = false
        };

        connectionBox.SelectionChanged += async (_, _) =>
        {
            if (connectionBox.SelectedItem is not ToolConnectionInfo picked)
            {
                return;
            }

            ctx.SetValue("toConnection", picked.Id);
            _databaseBox.ItemsSource = null;
            _databaseBox.IsEnabled = false;
            ctx.SetValue("toDatabase", null);
            UpdateTarget();

            var databases = await ctx.ListDatabasesAsync(picked.Id, CancellationToken.None);
            _databaseBox.ItemsSource = databases;
            _databaseBox.IsEnabled = databases.Count > 0;
        };
        _databaseBox.SelectionChanged += (_, _) =>
        {
            ctx.SetValue("toDatabase", _databaseBox.SelectedItem as string);
            UpdateTarget();
        };

        root.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,14,*"),
            Children = { Field("Copy to connection", connectionBox, 0), Field("Database", _databaseBox, 2) }
        });

        // ── What to copy (segmented) ──────────────────────────────────────────────────────────────────
        root.Children.Add(Section("WHAT TO COPY", Segmented(
            [(What.Both, "Structure + data"), (What.Structure, "Structure only"), (What.Data, "Data only")],
            What.Both, stretch: true, v => ctx.SetValue("what", v))));

        // ── Rows (segmented + flat number field) ──────────────────────────────────────────────────────
        var rowCount = new TextBox
        {
            Text = "1000", Width = 84, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, FontFamily = Mono
        };

        void SetRows(bool first) => ctx.SetValue("rows", first ? Digits(rowCount.Text, "1000") : "All");
        rowCount.TextChanged += (_, _) => { if (rowCount.IsEnabled) SetRows(true); };

        var rowsSeg = Segmented([("all", "All rows"), ("first", "First")], "all", stretch: false, v =>
        {
            var first = v == "first";
            rowCount.IsEnabled = first;
            rowCount.Opacity = first ? 1 : 0.5;
            SetRows(first);
        });
        rowCount.Opacity = 0.5;

        root.Children.Add(Section("ROWS", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children = { rowsSeg, rowCount }
        }));

        // ── Fidelity options (switches) ───────────────────────────────────────────────────────────────
        root.Children.Add(SwitchRow("Keep identity / sequence values", true,
            "Preserve the original keys instead of letting the target regenerate them",
            v => ctx.SetValue("keepIdentity", v ? "true" : "false")));
        root.Children.Add(SwitchRow("Drop target table if it exists", false,
            "Off by default — the copy fails safely if the target table already exists",
            v => ctx.SetValue("dropExisting", v ? "true" : "false")));

        // ── How (mode cards) ──────────────────────────────────────────────────────────────────────────
        root.Children.Add(Section("HOW", ModeCards(initialMode, v => ctx.SetValue("mode", v))));

        Content = new ScrollViewer { Content = root };
    }

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");

    private void UpdateTarget()
    {
        var db = _databaseBox.SelectedItem as string;
        _targetChip.Text = string.IsNullOrWhiteSpace(db) ? "—" : db;
    }

    private static class What
    {
        public const string Both = "Structure + data";
        public const string Structure = "Structure only";
        public const string Data = "Data only";
    }

    // ── Building blocks ───────────────────────────────────────────────────────────────────────────────

    private static Control Chip(string label, Control value)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeight.Bold, Opacity = 0.5,
                        LetterSpacing = 0.5, VerticalAlignment = VerticalAlignment.Center },
                    value
                }
            }
        };
        pill[!BackgroundProperty] = new DynamicResourceExtension("SESecondaryBgBrush");
        pill[!BorderBrushProperty] = new DynamicResourceExtension("SEHairlineBrush");
        return pill;
    }

    private static Control Field(string label, Control input, int column)
    {
        var panel = Labeled(label, input, uppercase: false);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private static Control Section(string label, Control body) => Labeled(label, body, uppercase: true);

    private static Control Labeled(string label, Control input, bool uppercase)
    {
        var caption = new TextBlock
        {
            Text = label, FontSize = uppercase ? 10.5 : 11.5, Margin = new Thickness(0, 0, 0, 6),
            LetterSpacing = uppercase ? 0.6 : 0, FontWeight = uppercase ? FontWeight.Bold : FontWeight.Normal
        };
        caption[!ForegroundProperty] = new DynamicResourceExtension(uppercase ? "SETextFaintBrush" : "SETextSecondaryBrush");
        return new StackPanel { Spacing = 0, Children = { caption, input } };
    }

    // A real segmented pill: Border segments inside a hairline track; the selected one gets the panel
    // background + emphasis. stretch=true fills the row (equal columns); false sizes to content (compact).
    private static Control Segmented(IReadOnlyList<(string Value, string Label)> options, string initial,
        bool stretch, Action<string> onChange)
    {
        var segments = new List<(Border Box, TextBlock Text, string Value)>();
        var grid = new Grid();
        for (var i = 0; i < options.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(stretch ? GridLength.Star : GridLength.Auto));
        }

        for (var i = 0; i < options.Count; i++)
        {
            var (value, label) = options[i];
            var text = new TextBlock
            {
                Text = label, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var box = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(stretch ? 6 : 14, 6),
                Cursor = new Cursor(StandardCursorType.Hand), Child = text
            };
            var captured = value;
            box.PointerPressed += (_, _) =>
            {
                onChange(captured);
                foreach (var (b, t, v) in segments)
                {
                    StyleSegment(b, t, v == captured);
                }
            };
            Grid.SetColumn(box, i);
            segments.Add((box, text, value));
            grid.Children.Add(box);
        }

        foreach (var (b, t, v) in segments)
        {
            StyleSegment(b, t, v == initial);
        }

        var track = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(3), BorderThickness = new Thickness(1),
            Child = grid, HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left
        };
        track[!BackgroundProperty] = new DynamicResourceExtension("SESecondaryBgBrush");
        track[!BorderBrushProperty] = new DynamicResourceExtension("SEHairlineBrush");
        return track;
    }

    private static void StyleSegment(Border box, TextBlock text, bool on)
    {
        if (on)
        {
            box[!BackgroundProperty] = new DynamicResourceExtension("SEPanelBgBrush");
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SETextPrimaryBrush");
            text.FontWeight = FontWeight.SemiBold;
        }
        else
        {
            box.Background = Brushes.Transparent;
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SETextSecondaryBrush");
            text.FontWeight = FontWeight.Normal;
        }
    }

    private static Control SwitchRow(string label, bool initial, string help, Action<bool> onChange)
    {
        var toggle = new ToggleSwitch { IsChecked = initial, OnContent = "", OffContent = "" };
        toggle.IsCheckedChanged += (_, _) => onChange(toggle.IsChecked == true);

        var helpText = new TextBlock { Text = help, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        helpText[!ForegroundProperty] = new DynamicResourceExtension("SETextFaintBrush");
        var text = new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = label, FontWeight = FontWeight.Medium }, helpText }
        };

        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { text, Put(toggle, 1) } };
    }

    private Control ModeCards(string initial, Action<string> onChange)
    {
        var cards = new List<(Border Card, string Value)>();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };

        Border MakeCard(string value, string title, string desc, int column)
        {
            var radio = new RadioButton { GroupName = "mode", IsChecked = value == initial, VerticalAlignment = VerticalAlignment.Top };
            var descText = new TextBlock { Text = desc, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            descText[!ForegroundProperty] = new DynamicResourceExtension("SETextSecondaryBrush");

            var card = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 11), BorderThickness = new Thickness(1.5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,9,*"),
                    Children =
                    {
                        radio,
                        Put(new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, descText } }, 2)
                    }
                }
            };

            void Select() { onChange(value); foreach (var (c, v) in cards) Highlight(c, v == value); }
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) Select(); };
            card.PointerPressed += (_, _) => radio.IsChecked = true;

            Grid.SetColumn(card, column);
            cards.Add((card, value));
            return card;
        }

        grid.Children.Add(MakeCard("Run the copy", "Run the copy", "Create & fill the table on the target, with progress", 0));
        grid.Children.Add(MakeCard("Open as script", "Open as script", "Review the CREATE + INSERT in a new query tab first", 2));
        foreach (var (c, v) in cards) Highlight(c, v == initial);
        return grid;
    }

    private static void Highlight(Border card, bool on)
    {
        card[!BorderBrushProperty] = new DynamicResourceExtension(on ? "SEAccentBrush" : "SEHairlineBrush");
        if (on)
        {
            card.Background = AccentWash;
        }
        else
        {
            card[!BackgroundProperty] = new DynamicResourceExtension("SEPanelBgBrush");
        }
    }

    private static string Digits(string? text, string fallback)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : fallback;
    }

    private static Control Put(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }
}
