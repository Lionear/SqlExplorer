using System.Collections.ObjectModel;
using Avalonia.Media;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// One node in the unified sidebar tree. The roots are saved connections; every node below
/// is produced lazily by the owning provider when the node is first expanded (DBeaver-style),
/// so a large server is never introspected all at once.
/// </summary>
public partial class TreeNodeViewModel : ViewModelBase
{
    // Fetches the children of a node: (connection, path-from-root-to-this-node) -> child descriptors.
    private readonly Func<SavedConnection, IReadOnlyList<DbNodeRef>, Task<IReadOnlyList<DbTreeNode>>>? _load;

    // The ancestry to hand the provider when loading THIS node's children (empty for a root).
    private readonly IReadOnlyList<DbNodeRef> _pathToChildren;

    // The owning connection's provider — null only for the "…" placeholder — read for
    // CreateCapabilities (DDL Create menu visibility); inherited down the subtree like Connection.
    private readonly IDbProvider? _provider;

    private bool _loaded;

    [ObservableProperty]
    private bool _isExpanded;

    // Connection-root state, shown as a status dot and gating Connect/Disconnect (root nodes only).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    private ConnectionState _state;

    private TreeNodeViewModel(
        SavedConnection connection,
        IDbProvider provider,
        DbNodeKind? kind,
        string name,
        string title,
        bool hasChildren,
        Geometry? iconGeometry,
        IImage? iconImage,
        IReadOnlyList<DbNodeRef> pathToChildren,
        Func<SavedConnection, IReadOnlyList<DbNodeRef>, Task<IReadOnlyList<DbTreeNode>>>? load)
    {
        Connection = connection;
        _provider = provider;
        NodeKind = kind;
        Name = name;
        Title = title;
        HasChildren = hasChildren;
        IconGeometry = iconGeometry;
        IconImage = iconImage;
        _pathToChildren = pathToChildren;
        _load = load;

        if (hasChildren)
        {
            Children.Add(Placeholder());
        }
    }

    private TreeNodeViewModel(string title)
    {
        Connection = null!;
        Name = title;
        Title = title;
        IsPlaceholder = true;
        _pathToChildren = [];
    }

    /// <summary>The saved connection this node belongs to (inherited down the whole subtree).</summary>
    public SavedConnection Connection { get; private set; }

    /// <summary>Null for the connection root; otherwise the kind of database object.</summary>
    public DbNodeKind? NodeKind { get; }

    /// <summary>The object's own name (unqualified, without the display detail).</summary>
    public string Name { get; private set; }

    public string Title { get; private set; }

    /// <summary>A vector line-icon drawn when there is no <see cref="IconImage"/>.</summary>
    public Geometry? IconGeometry { get; }

    /// <summary>A rendered image icon (provider brand logo); wins over the vector icon when set.</summary>
    public IImage? IconImage { get; }

    public bool HasImageIcon => IconImage is not null;

    public bool HasVectorIcon => IconImage is null && IconGeometry is not null;

    public bool HasChildren { get; private set; }

    public bool IsConnectionNode => NodeKind is null && !IsPlaceholder;

    /// <summary>Accent brush for a colour-flagged connection root (prod = red); null when unset/invalid.</summary>
    public IBrush? ConnectionColorBrush =>
        IsConnectionNode && Connection.Color is { } hex && Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color)
            : null;

    public bool HasConnectionColor => ConnectionColorBrush is not null;

    /// <summary>Connect is offered on a connection root that isn't currently connected.</summary>
    public bool CanConnect => IsConnectionNode && State != ConnectionState.Connected;

    /// <summary>Disconnect is offered on a connected connection root.</summary>
    public bool CanDisconnect => IsConnectionNode && State == ConnectionState.Connected;

    public bool IsTableOrView => NodeKind is DbNodeKind.Table or DbNodeKind.View;

    public bool IsColumn => NodeKind is DbNodeKind.Column;

    public bool IsCopyable => IsTableOrView || IsColumn
        || NodeKind is DbNodeKind.Index or DbNodeKind.Sequence or DbNodeKind.Object;

    public bool IsPlaceholder { get; }

    /// <summary>DDL Create menu visibility (1-to-1 with the provider's declared <c>CreateCapabilities</c>):
    /// "New Database…" on a connection root, "New Schema…"/"New Table…" on the node kind the provider
    /// says each belongs under. False (never shown) for providers/positions with no such capability.</summary>
    public bool CanCreateDatabase => _provider is not null && IsConnectionNode
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Database && c.ParentNode is null);

    public bool CanCreateSchema => _provider is not null && NodeKind is { } kind
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Schema && c.ParentNode == kind);

    public bool CanCreateTable => _provider is not null && NodeKind is { } kind
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Table && c.ParentNode == kind);

    // DROP/ALTER menu visibility (host-only, no SDK — see Core/Ddl/AlterStatementBuilder): reuses the
    // same CreateCapabilities the provider already declares, since every engine here can drop exactly
    // what it can create. Gated on the node itself BEING that kind (unlike CanCreate*, which gates on
    // the node it would be created UNDER).
    public bool CanDropDatabase => _provider is not null && NodeKind == DbNodeKind.Database
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Database);

    public bool CanDropSchema => _provider is not null && NodeKind == DbNodeKind.Schema
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Schema);

    public bool CanDropTable => _provider is not null && IsTableOrView
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Table);

    /// <summary>"Add Column…" only on an actual table (not a view — ALTER TABLE ADD doesn't apply there).</summary>
    public bool CanAddColumn => _provider is not null && NodeKind == DbNodeKind.Table
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Table);

    public bool CanDropColumn => _provider is not null && IsColumn
        && _provider.CreateCapabilities.Any(c => c.Kind == DbObjectKind.Table);

    public bool CanRenameColumn => CanDropColumn;

    /// <summary>"Import CSV…" — any table the provider can also ALTER (i.e. a real writable table).</summary>
    public bool CanImportCsv => CanAddColumn;

    /// <summary>Owning schema, if this node sits under one (null for schema-less engines like SQLite).</summary>
    public string? SchemaName => _pathToChildren.FirstOrDefault(r => r.Kind == DbNodeKind.Schema)?.Name;

    /// <summary>Owning database/catalog, if this node sits under one — drives execute-time catalog
    /// context so a browse/query runs against the right database, not the connection default.</summary>
    public string? DatabaseName => _pathToChildren.FirstOrDefault(r => r.Kind == DbNodeKind.Database)?.Name;

    /// <summary>Owning table/view, for a column node (used by Add/Drop/Rename Column).</summary>
    public string? TableName => _pathToChildren.FirstOrDefault(r => r.Kind is DbNodeKind.Table or DbNodeKind.View)?.Name;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Build a root node for a saved connection. A provider brand image wins; otherwise a
    /// generic connection line-icon is shown.</summary>
    public static TreeNodeViewModel ForConnection(
        SavedConnection connection,
        IDbProvider provider,
        IImage? iconImage,
        Func<SavedConnection, IReadOnlyList<DbNodeRef>, Task<IReadOnlyList<DbTreeNode>>> load) =>
        new(connection, provider, kind: null, connection.Name, connection.Name, hasChildren: true,
            NodeIcons.Connection, iconImage, pathToChildren: [], load);

    /// <summary>
    /// Update a connection root's saved data in place (after editing) WITHOUT tearing down its loaded
    /// subtree, so an open connection stays open. Future (re)loads use the new parameters; the live
    /// subtree keeps the old ones until the next reconnect. Only valid on a connection root whose
    /// provider is unchanged.
    /// </summary>
    public void UpdateConnection(SavedConnection connection)
    {
        Connection = connection;
        Name = connection.Name;
        Title = connection.Name;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ConnectionColorBrush));
        OnPropertyChanged(nameof(HasConnectionColor));
    }

    /// <summary>Reload this node's children from scratch (e.g. the Connect/Refresh action).</summary>
    public async Task RefreshAsync()
    {
        _loaded = false;
        Children.Clear();
        Children.Add(Placeholder());
        await LoadAsync();
        IsExpanded = true;
    }

    /// <summary>Drop the loaded subtree and mark the connection root disconnected.</summary>
    public void Disconnect()
    {
        _loaded = false;
        IsExpanded = false;
        Children.Clear();
        HasChildren = true;
        Children.Add(Placeholder());
        State = ConnectionState.Disconnected;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && HasChildren)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_load is null)
        {
            return;
        }

        _loaded = true;
        if (IsConnectionNode)
        {
            State = ConnectionState.Connecting;
        }

        try
        {
            var children = await _load(Connection, _pathToChildren);

            Children.Clear();
            foreach (var child in children)
            {
                var title = child.Detail is null ? child.Name : $"{child.Name} : {child.Detail}";
                var childPath = new List<DbNodeRef>(_pathToChildren) { new(child.Kind, child.Name) };
                Children.Add(new TreeNodeViewModel(
                    Connection, _provider!, child.Kind, child.Name, title, child.HasChildren,
                    NodeIcons.For(child.Kind), iconImage: null, childPath, _load));
            }

            // No children came back -> drop the expander so the node reads as a leaf.
            if (Children.Count == 0)
            {
                HasChildren = false;
            }

            if (IsConnectionNode)
            {
                State = ConnectionState.Connected;
            }
        }
        catch
        {
            // Allow a retry on the next expand; surface the failure on the root's status dot.
            _loaded = false;
            Children.Clear();
            if (IsConnectionNode)
            {
                State = ConnectionState.Error;
            }
        }
    }

    private static TreeNodeViewModel Placeholder() => new("…");
}
