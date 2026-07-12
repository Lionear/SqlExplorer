using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// The window shell: the connection sidebar tree plus a set of editor tabs
/// (<see cref="DocumentViewModel"/>). Query/result/save-flow logic lives in the documents;
/// this VM owns connections, the tree and tab management.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IDbProviderRegistry _providers;
    private readonly ISqlFormatter _formatter;
    private readonly ConnectionService _connections;
    private readonly Func<ConnectionDialogViewModel> _dialogFactory;

    // Selected tree node drives the active connection: any node knows its owning connection.
    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private SavedConnection? _selectedConnection;

    [ObservableProperty]
    private DocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private string _status = string.Empty;

    public MainViewModel(
        IDbProviderRegistry providers,
        ISqlFormatter formatter,
        ConnectionService connections,
        Func<ConnectionDialogViewModel> dialogFactory,
        ILocalizer localizer)
    {
        _providers = providers;
        _formatter = formatter;
        _connections = connections;
        _dialogFactory = dialogFactory;
        Loc = localizer;

        RefreshConnections();
    }

    public ILocalizer Loc { get; }

    /// <summary>The sidebar tree: one root node per saved connection, children loaded lazily.</summary>
    public ObservableCollection<TreeNodeViewModel> ConnectionNodes { get; } = [];

    /// <summary>Open editor tabs (query panes and table-browse panes).</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Set by the view so the VM can request a modal connection dialog.</summary>
    public Func<ConnectionDialogViewModel, Task<SavedConnection?>>? ConnectionDialogRequested { get; set; }

    /// <summary>Set by the view so the VM can copy text to the OS clipboard.</summary>
    public Func<string, Task>? ClipboardRequested { get; set; }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) =>
        SelectedConnection = value?.Connection;

    private void RefreshConnections(string? selectId = null)
    {
        ConnectionNodes.Clear();
        foreach (var connection in _connections.List())
        {
            ConnectionNodes.Add(TreeNodeViewModel.ForConnection(connection, ResolveIconImage(connection.Kind), LoadNodeChildrenAsync));
        }

        SelectedNode = selectId is null
            ? null
            : ConnectionNodes.FirstOrDefault(n => n.Connection.Id == selectId);
    }

    // A provider may ship a brand image and/or a glyph. We render the image when the host can decode
    // it; the tree otherwise falls back to a generic connection line-icon. The glyph is left to hosts
    // that can render emoji (Linux/Avalonia can't), so it is not used here. SVG needs an extra
    // renderer, so raster only for now.
    private IImage? ResolveIconImage(DatabaseKind kind)
    {
        var icon = _providers.Get(kind).Icon;
        if (icon?.ImageData is not { Length: > 0 } bytes || !CanRenderImage(icon.ImageMediaType))
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static bool CanRenderImage(string? mediaType) =>
        mediaType is not null
        && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    // The lazy loader every tree node calls on expand: resolve secrets, ask the provider.
    private async Task<IReadOnlyList<DbTreeNode>> LoadNodeChildrenAsync(
        SavedConnection connection,
        IReadOnlyList<DbNodeRef> ancestors)
    {
        try
        {
            var profile = _connections.Resolve(connection);
            var nodes = await _providers.Get(connection.Kind).GetChildNodesAsync(profile, ancestors, CancellationToken.None);
            if (ancestors.Count == 0)
            {
                Status = Loc.Get("StatusConnected", nodes.Count);
            }

            return nodes;
        }
        catch (Exception ex)
        {
            // Surface the message and let the tree node mark itself errored (root shows a red status dot).
            Status = ex.Message;
            throw;
        }
    }

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        if (ConnectionDialogRequested is null)
        {
            return;
        }

        var saved = await ConnectionDialogRequested(_dialogFactory());
        if (saved is not null)
        {
            RefreshConnections(saved.Id);
        }
    }

    [RelayCommand]
    private async Task EditConnectionAsync()
    {
        if (SelectedConnection is null || ConnectionDialogRequested is null)
        {
            return;
        }

        var dialog = _dialogFactory();
        dialog.LoadForEdit(SelectedConnection);

        var saved = await ConnectionDialogRequested(dialog);
        if (saved is not null)
        {
            RefreshConnections(saved.Id);
        }
    }

    [RelayCommand]
    private void DeleteConnection()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        _connections.Delete(SelectedConnection.Id);
        RefreshConnections();
    }

    // Copy the selected connection (secret included) under a "… (copy)" name, then select the copy.
    [RelayCommand]
    private void DuplicateConnection()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var copy = _connections.Duplicate(SelectedConnection.Id, $"{SelectedConnection.Name} {Loc["CopySuffix"]}");
        RefreshConnections(copy.Id);
    }

    // Connect = (re)load the selected connection's tree from the root and expand it.
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var root = ConnectionNodes.FirstOrDefault(n => n.Connection.Id == SelectedConnection.Id);
        if (root is not null)
        {
            await root.RefreshAsync();
        }
    }

    // Collapse and forget the selected connection's tree; its status dot goes back to grey.
    [RelayCommand]
    private void Disconnect()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var root = ConnectionNodes.FirstOrDefault(n => n.Connection.Id == SelectedConnection.Id);
        root?.Disconnect();
    }

    /// <summary>Open a new, empty query tab against the selected connection.</summary>
    [RelayCommand]
    private void NewQueryTab()
    {
        var connection = SelectedConnection ?? _connections.List().FirstOrDefault();
        if (connection is null)
        {
            return;
        }

        var document = NewDocument();
        document.InitQuery(connection);
        AddDocument(document);
    }

    // Table/view context action or double-click: open (or focus) a browse tab for that table.
    [RelayCommand]
    private async Task BrowseTableAsync(CancellationToken ct)
    {
        if (SelectedNode is not { IsTableOrView: true } node)
        {
            return;
        }

        var existing = Documents.FirstOrDefault(d => d.MatchesBrowse(node.Connection.Id, node.SchemaName, node.Name));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }

        var document = NewDocument();
        document.InitBrowse(node.Connection, node.SchemaName, node.Name);
        AddDocument(document);
        await document.LoadPageAsync(ct);
    }

    [RelayCommand]
    private void CloseTab(DocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        var index = Documents.IndexOf(document);
        Documents.Remove(document);

        if (SelectedDocument == document)
        {
            SelectedDocument = Documents.Count == 0
                ? null
                : Documents[Math.Min(index, Documents.Count - 1)];
        }
    }

    // Copy a dialect-quoted identifier so it pastes straight into SQL — crucial on Postgres, where
    // an unquoted PascalCase name folds to lowercase and fails (42703). Tables/views come schema-qualified.
    [RelayCommand]
    private async Task CopyNameAsync()
    {
        if (SelectedNode is not { IsCopyable: true } node || SelectedConnection is null || ClipboardRequested is null)
        {
            return;
        }

        var dialect = _providers.Get(SelectedConnection.Kind).Dialect;
        var text = node.IsTableOrView && node.SchemaName is { } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(node.Name)}"
            : dialect.QuoteIdentifier(node.Name);

        await ClipboardRequested(text);
    }

    // Generate a SQL template (SELECT/INSERT/UPDATE/…) for the selected table into a new query tab —
    // the DBeaver/DataGrip "SQL commands" action. Column-based templates read the live column metadata.
    [RelayCommand]
    private async Task GenerateSqlAsync(string? kind)
    {
        if (SelectedNode is not { IsTableOrView: true } node)
        {
            return;
        }

        var connection = node.Connection;
        var dialect = _providers.Get(connection.Kind).Dialect;
        var qualified = node.SchemaName is { } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(node.Name)}"
            : dialect.QuoteIdentifier(node.Name);

        try
        {
            var sql = kind switch
            {
                "Select" => $"SELECT * FROM {qualified};",
                "Count" => $"SELECT COUNT(*) FROM {qualified};",
                _ => SqlTemplateBuilder.Build(kind ?? "Select", qualified, dialect, await FetchColumnsAsync(connection, qualified))
            };

            var document = NewDocument();
            document.InitQuery(connection);
            document.Sql = sql;
            AddDocument(document);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    // A zero-row query is the cheapest way to read a relation's column metadata (names, PK) per dialect.
    private async Task<IReadOnlyList<ResultColumn>> FetchColumnsAsync(SavedConnection connection, string qualified)
    {
        var provider = _providers.Get(connection.Kind);
        var profile = _connections.Resolve(connection);
        // One row is enough for the column schema and stays valid across dialects — SQL Server's
        // OFFSET/FETCH rejects FETCH NEXT 0, so a zero-row probe is not portable.
        var probe = provider.Dialect.Paginate($"SELECT * FROM {qualified}", 1, 0);
        var result = await provider.ExecuteQueryAsync(profile, probe, CancellationToken.None);
        return result.Columns;
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var next = Loc.Culture.TwoLetterISOLanguageName == "nl"
            ? CultureInfo.GetCultureInfo("en")
            : CultureInfo.GetCultureInfo("nl");

        Loc.SetCulture(next);
    }

    private DocumentViewModel NewDocument() => new(_providers, _connections, _formatter, Loc);

    private void AddDocument(DocumentViewModel document)
    {
        Documents.Add(document);
        SelectedDocument = document;
    }
}
