using Avalonia;
using Avalonia.Controls;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using SqlExplorer.Sdk.Localization;

namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Copy Table's own dialog view (Route B), and — via <see cref="IToolDialogLifecycle"/> — the owner of the
/// <b>whole</b> dialog lifecycle: input → progress → done. The host hides its generic checklist, log,
/// progress bar and action bar for this dialog, so every state here is rendered to the SE-188 mockup: a
/// fixed From → To header, then either the input form, a stepped progress list (done / running-with-bar /
/// pending glyphs, right-aligned per-step detail) or a result banner, each with its own footer.
///
/// <para>Input values are written back through <see cref="IToolUiContext"/> under the same
/// <c>ToolField.Key</c>s <see cref="CopyTableTool"/> reads; the run itself is driven from this view's own
/// buttons through <c>RunAsync</c>/<c>CancelRun</c>/<c>CloseDialog</c>. Segments and cards are code-built
/// from Borders (not Fluent radios) for full styling control, on DynamicResource theme brushes so the
/// dialog follows the app's light/dark theme.</para>
/// </summary>
public sealed class CopyTableView : UserControl, IToolDialogLifecycle
{
    private readonly IToolUiContext _ctx;
    private readonly IPluginLocalizer _loc;
    private readonly string _sourceTable;

    private readonly TextBlock _targetChip;
    private readonly ComboBox _databaseBox;

    // The three body states and the three footers — exactly one of each is visible at a time.
    private readonly Panel _body = new();
    private readonly Control _inputBody;
    private readonly StackPanel _stepList = new() { Spacing = 0 };
    private readonly Control _progressBody;
    private readonly Border _resultCard;
    private readonly TextBlock _resultText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _resultGlyph = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10) };
    private readonly Button _openTargetLink;
    private readonly Control _resultBody;

    private readonly Panel _footer = new();
    private readonly Control _inputFooter;
    private readonly Control _runningFooter;
    private readonly Control _resultFooter;
    private readonly Button _runButton;
    private readonly TextBlock _runningNote;
    private readonly TextBlock _resultNote;
    private readonly Button _againButton;

    private readonly List<StepRow> _steps = [];

    private CopySummary? _summary;
    private bool _scriptMode;

    // Accent tint for a selected card — a low-alpha wash of the accent that reads on both light and dark.
    private static readonly IBrush AccentWash = new SolidColorBrush(Color.Parse("#223574F0"));
    private static readonly IBrush OkWash = new SolidColorBrush(Color.Parse("#243FB950"));
    private static readonly IBrush ErrorWash = new SolidColorBrush(Color.Parse("#26D64545"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D64545"));

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");

    /// <summary>Raised by the done state's "Open target table" link; the tool wires it to a query tab on the
    /// destination (it holds the host seam, the view doesn't).</summary>
    public event Action? OpenTargetRequested;

    public CopyTableView(IToolUiContext ctx, string initialMode, string sourceTable)
    {
        _ctx = ctx;
        _loc = ctx.Localizer;
        _sourceTable = sourceTable;

        // Seed the values the host will collect, so a straight-through "Run" uses the shown defaults.
        ctx.SetValue("what", What.Both);
        ctx.SetValue("rows", "All");
        ctx.SetValue("keepIdentity", "true");
        ctx.SetValue("dropExisting", "false");
        ctx.SetValue("mode", initialMode);
        _scriptMode = initialMode == Modes.Script;

        // ── From → To header (fixed across every state) ───────────────────────────────────────────────
        _targetChip = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, FontFamily = Mono, FontSize = 12.5 };
        var header = new Border
        {
            Padding = new Thickness(20, 14), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Chip(L("copy.ui.from"), new TextBlock { Text = sourceTable, FontFamily = Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }),
                    new TextBlock { Text = "→", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 },
                    Chip(L("copy.ui.to"), _targetChip)
                }
            }
        };
        header[!BorderBrushProperty] = new DynamicResourceExtension("SEHairlineBrush");

        // ── Body states ───────────────────────────────────────────────────────────────────────────────
        _databaseBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = L("copy.ui.pickDatabase"), IsEnabled = false
        };
        _runButton = new Button { Content = L("copy.ui.btn.run"), IsEnabled = false, Classes = { "Accent" } };
        _runButton.Click += async (_, _) => await _ctx.RunAsync();
        _inputBody = BuildInput(ctx, initialMode);

        _progressBody = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Margin = new Thickness(20, 16), Child = _stepList },
            IsVisible = false
        };

        _openTargetLink = LinkButton(L("copy.ui.link.openTarget"), () => OpenTargetRequested?.Invoke());
        _resultCard = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(13, 11), BorderThickness = new Thickness(1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,11,*,Auto"),
                Children = { _resultGlyph, Put(_resultText, 2), Put(_openTargetLink, 3) }
            }
        };
        _resultBody = new Border
        {
            Margin = new Thickness(20, 16), Child = _resultCard, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Top
        };

        _body.Children.Add(_inputBody);
        _body.Children.Add(_progressBody);
        _body.Children.Add(_resultBody);

        // ── Footers ───────────────────────────────────────────────────────────────────────────────────
        _inputFooter = Footer(L("copy.ui.note.input"), out _,
            (L("copy.ui.btn.cancel"), false, () => _ctx.CloseDialog()), _runButton);

        var busyButton = new Button { Content = L("copy.ui.btn.copying"), IsEnabled = false, Classes = { "Accent" } };
        _runningFooter = Footer(L("copy.ui.note.running"), out _runningNote,
            (L("copy.ui.btn.cancel"), false, () => _ctx.CancelRun()), busyButton);
        _runningFooter.IsVisible = false;

        _againButton = new Button { Content = L("copy.ui.btn.again"), Classes = { "Accent" } };
        _againButton.Click += (_, _) => ShowInput();
        _resultFooter = Footer(L("copy.ui.note.done"), out _resultNote,
            (L("copy.ui.btn.close"), false, () => _ctx.CloseDialog()), _againButton, tintedNote: true);
        _resultFooter.IsVisible = false;

        _footer.Children.Add(_inputFooter);
        _footer.Children.Add(_runningFooter);
        _footer.Children.Add(_resultFooter);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { header, PutRow(_body, 1), PutRow(_footer, 2) }
        };

        _ = LoadRowEstimateAsync();
    }

    // ── Lifecycle (host-driven) ───────────────────────────────────────────────────────────────────────

    public void OnRunStarted()
    {
        _summary = null;
        BuildSteps();
        _inputBody.IsVisible = false;
        _resultBody.IsVisible = false;
        _progressBody.IsVisible = true;
        _inputFooter.IsVisible = false;
        _resultFooter.IsVisible = false;
        _runningFooter.IsVisible = true;
        _runningNote.Text = L(_scriptMode ? "copy.ui.note.scripting" : "copy.ui.note.running");
    }

    public void OnProgress(ToolProgress progress)
    {
        if (progress.ItemKey is not { } key)
        {
            return;
        }

        var step = _steps.FirstOrDefault(s => s.Key == key);
        if (step is null)
        {
            // A step the plan didn't foresee (an engine-specific extra) still shows up rather than vanishing.
            step = new StepRow(key, progress.Message);
            _steps.Add(step);
            _stepList.Children.Add(step.Root);
        }

        step.Update(progress.ItemStatus, progress.Detail, progress.Fraction);
    }

    public void OnRunFinished(ToolRunOutcome outcome, string? message)
    {
        // Anything still spinning when the run ends didn't get there — leave no step lying about it.
        foreach (var step in _steps.Where(s => s.IsRunning))
        {
            step.Update(outcome == ToolRunOutcome.Succeeded ? ToolItemStatus.Done : ToolItemStatus.Error, null, null);
        }

        var (wash, border, glyph, note, text) = outcome switch
        {
            ToolRunOutcome.Succeeded => (OkWash, OkBrush, OkBrush, L("copy.ui.note.done"), SuccessText()),
            ToolRunOutcome.Cancelled => (ErrorWash, ErrorBrush, ErrorBrush, L("copy.ui.note.cancelled"),
                L("copy.ui.banner.cancelled")),
            _ => (ErrorWash, ErrorBrush, ErrorBrush, L("copy.ui.note.failed"),
                message ?? L("copy.ui.banner.failed"))
        };

        _resultCard.Background = wash;
        _resultCard.BorderBrush = border;
        _resultGlyph.Background = glyph;
        _resultGlyph.Child = outcome == ToolRunOutcome.Succeeded ? CheckMark(11, Brushes.White) : CrossMark(10, Brushes.White);
        _resultText.Text = text;
        _resultNote.Text = note;
        _resultNote.Foreground = outcome == ToolRunOutcome.Succeeded ? OkBrush : ErrorBrush;
        // "Open target table" only makes sense for a copy that actually landed a table.
        _openTargetLink.IsVisible = outcome == ToolRunOutcome.Succeeded && _summary is { Scripted: false };
        _againButton.Content = L(outcome == ToolRunOutcome.Succeeded ? "copy.ui.btn.again" : "copy.ui.btn.back");

        _progressBody.IsVisible = false;
        _resultBody.IsVisible = true;
        _runningFooter.IsVisible = false;
        _resultFooter.IsVisible = true;
    }

    /// <summary>Called by the tool just before it returns, so the done banner can name what actually
    /// happened ("Copied customers → analytics.customers · 5,000 rows in 2.1 s").</summary>
    public void SetSummary(CopySummary summary) => _summary = summary;

    private string SuccessText()
    {
        if (_summary is not { } s)
        {
            return L("copy.ui.banner.generic");
        }

        var seconds = $"{s.Elapsed.TotalSeconds:0.0} s";
        return s.Scripted
            ? _loc.Get("copy.ui.banner.scripted", s.Source, s.Target, s.Rows.ToString("N0"))
            : _loc.Get("copy.ui.banner.copied", s.Source, s.Target, s.Rows.ToString("N0"), seconds);
    }

    private void ShowInput()
    {
        _resultBody.IsVisible = false;
        _progressBody.IsVisible = false;
        _inputBody.IsVisible = true;
        _resultFooter.IsVisible = false;
        _runningFooter.IsVisible = false;
        _inputFooter.IsVisible = true;
    }

    // The plan is known before the first report: which steps run depends on what's being copied and how.
    private void BuildSteps()
    {
        _steps.Clear();
        _stepList.Children.Clear();

        var what = _ctx.GetValue("what") ?? What.Both;
        _scriptMode = (_ctx.GetValue("mode") ?? Modes.Run) == Modes.Script;

        void Add(string key, string labelKey)
        {
            var row = new StepRow(key, L(labelKey));
            _steps.Add(row);
            _stepList.Children.Add(row.Root);
        }

        Add("schema", "copy.ui.step.schema");
        if (_scriptMode)
        {
            Add("script", "copy.ui.step.script");
            return;
        }

        if (what != What.Data)
        {
            Add("create", "copy.ui.step.create");
        }

        if (what != What.Structure)
        {
            Add("rows", "copy.ui.step.rows");
        }

        Add("done", "copy.ui.step.done");
    }

    // ── Input state ───────────────────────────────────────────────────────────────────────────────────

    private readonly TextBlock _estimate = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

    private Control BuildInput(IToolUiContext ctx, string initialMode)
    {
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 12), Spacing = 18 };

        // ── Destination connection + database ─────────────────────────────────────────────────────────
        var connectionBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ctx.ListConnections(),
            PlaceholderText = L("copy.ui.pickConnection"),
            // Bind (not a captured value): the closed selection box rebuilds the template and a captured value
            // renders blank there — a binding to Name resolves for both the dropdown row and the selection box.
            ItemTemplate = new FuncDataTemplate<ToolConnectionInfo>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(ToolConnectionInfo.Name)) }, supportsRecycling: false)
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
            Children = { Field(L("copy.ui.connection"), connectionBox, 0), Field(L("copy.ui.database"), _databaseBox, 2) }
        });

        // ── What to copy (segmented) ──────────────────────────────────────────────────────────────────
        root.Children.Add(Section(L("copy.ui.what"), Segmented(
            [(What.Both, L("copy.ui.what.both")), (What.Structure, L("copy.ui.what.structure")), (What.Data, L("copy.ui.what.data"))],
            What.Both, stretch: true, v => ctx.SetValue("what", v))));

        // ── Rows (segmented + flat number field + source estimate) ────────────────────────────────────
        var rowCount = new TextBox
        {
            Text = "1000", Width = 84, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, FontFamily = Mono
        };

        void SetRows(bool first) => ctx.SetValue("rows", first ? Digits(rowCount.Text, "1000") : "All");
        rowCount.TextChanged += (_, _) => { if (rowCount.IsEnabled) SetRows(true); };

        var rowsSeg = Segmented([("all", L("copy.ui.rows.all")), ("first", L("copy.ui.rows.first"))], "all", stretch: false, v =>
        {
            var first = v == "first";
            rowCount.IsEnabled = first;
            rowCount.Opacity = first ? 1 : 0.5;
            SetRows(first);
        });
        rowCount.Opacity = 0.5;
        _estimate[!ForegroundProperty] = new DynamicResourceExtension("SETextFaintBrush");

        root.Children.Add(Section(L("copy.ui.rows"), new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,12,Auto,*"),
            Children =
            {
                rowsSeg, Put(rowCount, 2),
                Put(new Border { Child = _estimate, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 3)
            }
        }));

        // ── Fidelity options (switches) ───────────────────────────────────────────────────────────────
        root.Children.Add(SwitchRow(L("copy.ui.keepIdentity"), true, L("copy.ui.keepIdentity.help"),
            v => ctx.SetValue("keepIdentity", v ? "true" : "false")));
        root.Children.Add(SwitchRow(L("copy.ui.dropExisting"), false, L("copy.ui.dropExisting.help"),
            v => ctx.SetValue("dropExisting", v ? "true" : "false")));

        // ── How (mode cards) ──────────────────────────────────────────────────────────────────────────
        root.Children.Add(Section(L("copy.ui.how"), ModeCards(initialMode, v =>
        {
            ctx.SetValue("mode", v);
            _scriptMode = v == Modes.Script;
            _runButton.Content = L(_scriptMode ? "copy.ui.btn.script" : "copy.ui.btn.run");
        })));

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = root
        };
    }

    // Best-effort row count for the "~N rows in source" hint; a source that won't answer just shows nothing.
    private async Task LoadRowEstimateAsync()
    {
        try
        {
            var qualified = _ctx.Provider.Dialect.QualifyName(_ctx.Profile.Database, null, _sourceTable);
            var result = await _ctx.QueryAsync($"SELECT COUNT(*) FROM {qualified}", CancellationToken.None);
            if (result.Rows.Count > 0 && result.Rows[0].Length > 0
                && long.TryParse(result.Rows[0][0]?.ToString(), out var count))
            {
                _estimate.Text = _loc.Get("copy.ui.estimate", count.ToString("N0"));
            }
        }
        catch
        {
            // No estimate is better than an error box over a cosmetic hint.
        }
    }

    private void UpdateTarget()
    {
        var db = _databaseBox.SelectedItem as string;
        var chosen = !string.IsNullOrWhiteSpace(db);
        _targetChip.Text = chosen ? $"{db}.{_sourceTable}" : "—";
        _runButton.IsEnabled = chosen;
    }

    private string L(string key) => _loc[key];

    private static class What
    {
        public const string Both = "Structure + data";
        public const string Structure = "Structure only";
        public const string Data = "Data only";
    }

    private static class Modes
    {
        public const string Run = "Run the copy";
        public const string Script = "Open as script";
    }

    // ── Progress step row ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One line of the progress checklist: glyph + label + right-aligned detail, with a slim bar
    /// underneath while that step is the running one and reports a fraction.</summary>
    private sealed class StepRow
    {
        private readonly Panel _glyph = new() { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _label;
        private readonly TextBlock _detail;
        private readonly ProgressBar _bar;

        public StepRow(string key, string label)
        {
            Key = key;
            _label = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            _detail = new TextBlock
            {
                FontSize = 12, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _detail[!ForegroundProperty] = new DynamicResourceExtension("SETextSecondaryBrush");

            _bar = new ProgressBar
            {
                Height = 5, Minimum = 0, Maximum = 1, Margin = new Thickness(33, 0, 4, 4), IsVisible = false,
                IsIndeterminate = false
            };

            var line = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("18,11,*,Auto"), Margin = new Thickness(4, 9),
                Children = { _glyph, Put(_label, 2), Put(_detail, 3) }
            };

            Root = new StackPanel { Children = { line, _bar } };
            SetGlyph(null);
        }

        public string Key { get; }

        public Control Root { get; }

        public bool IsRunning { get; private set; }

        public void Update(ToolItemStatus? status, string? detail, double? fraction)
        {
            if (status is { } s)
            {
                IsRunning = s == ToolItemStatus.Running;
                SetGlyph(s);
                _label.Opacity = 1;
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                _detail.Text = detail;
            }

            _bar.IsVisible = IsRunning && fraction is not null;
            if (fraction is { } f)
            {
                _bar.Value = Math.Clamp(f, 0, 1);
            }
        }

        // Done = filled green check, running = accent dot in a soft ring, error = red cross, pending = hollow.
        private void SetGlyph(ToolItemStatus? status)
        {
            _glyph.Children.Clear();
            switch (status)
            {
                case ToolItemStatus.Done:
                    _glyph.Children.Add(new Border
                    {
                        Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = OkBrush,
                        Child = CheckMark(10, Brushes.White)
                    });
                    break;
                case ToolItemStatus.Error:
                    _glyph.Children.Add(new Border
                    {
                        Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = ErrorBrush,
                        Child = CrossMark(9, Brushes.White)
                    });
                    break;
                case ToolItemStatus.Running:
                    var ring = new Border
                    {
                        Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = AccentWash,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                    var dot = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6) };
                    dot[!BackgroundProperty] = new DynamicResourceExtension("SEAccentBrush");
                    ring.Child = dot;
                    _glyph.Children.Add(ring);
                    break;
                case ToolItemStatus.Skipped:
                    _glyph.Children.Add(Hollow(0.35));
                    _label.Opacity = 0.55;
                    break;
                default:
                    _glyph.Children.Add(Hollow(0.7));
                    _label.Opacity = 0.55;
                    break;
            }
        }

        private static Control Hollow(double opacity)
        {
            var circle = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Opacity = opacity
            };
            circle[!BorderBrushProperty] = new DynamicResourceExtension("SETextFaintBrush");
            return circle;
        }
    }

    // ── Building blocks ───────────────────────────────────────────────────────────────────────────────

    private static Control CheckMark(double size, IBrush stroke) => new AvaloniaPath
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = stroke, StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Data = Geometry.Parse("M3,8 L6.5,11 L13,4"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private static Control CrossMark(double size, IBrush stroke) => new AvaloniaPath
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = stroke, StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round, Data = Geometry.Parse("M4,4 L12,12 M12,4 L4,12"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    // A footer bar: hairline top, panel background, a muted note on the left and the buttons on the right.
    // tintedNote: the result footer recolours its note per outcome, so it must NOT carry a theme-brush
    // binding — in Avalonia a live DynamicResource binding keeps winning over a later direct assignment.
    private static Control Footer(string note, out TextBlock noteBlock, (string Label, bool Accent, Action OnClick) secondary,
        Button primary, bool tintedNote = false)
    {
        noteBlock = new TextBlock
        {
            Text = note, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0)
        };
        if (!tintedNote)
        {
            noteBlock[!ForegroundProperty] = new DynamicResourceExtension("SETextFaintBrush");
        }

        var second = new Button { Content = secondary.Label, Classes = { "Subtle" }, Margin = new Thickness(0, 0, 8, 0) };
        second.Click += (_, _) => secondary.OnClick();

        var bar = new Border
        {
            Padding = new Thickness(18, 12), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children = { noteBlock, Put(second, 1), Put(primary, 2) }
            }
        };
        bar[!BackgroundProperty] = new DynamicResourceExtension("SEPanelBgBrush");
        bar[!BorderBrushProperty] = new DynamicResourceExtension("SEHairlineBrush");
        return bar;
    }

    private static Button LinkButton(string text, Action onClick)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SEAccentBrush");

        var button = new Button
        {
            Content = label, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2), Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => onClick();
        return button;
    }

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
            Text = uppercase ? label.ToUpperInvariant() : label,
            FontSize = uppercase ? 10.5 : 11.5, Margin = new Thickness(0, 0, 0, 6),
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

        grid.Children.Add(MakeCard(Modes.Run, L("copy.ui.mode.run"), L("copy.ui.mode.run.help"), 0));
        grid.Children.Add(MakeCard(Modes.Script, L("copy.ui.mode.script"), L("copy.ui.mode.script.help"), 2));
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

    private static Control PutRow(Control c, int row)
    {
        Grid.SetRow(c, row);
        return c;
    }
}

/// <summary>What the finished run actually did, handed from the tool to the view for the done banner.</summary>
public sealed record CopySummary(string Source, string Target, int Rows, TimeSpan Elapsed, bool Scripted);
