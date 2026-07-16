using SqlExplorer.Core.Ddl;
using SqlExplorer.Core.Localization;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Ddl;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlExplorer.App.ViewModels;

/// <summary>What <see cref="AlterObjectDialogViewModel"/> is confirming — a DROP (no extra input beyond
/// the SQL preview) or a column ALTER (needs a name/type input).</summary>
public enum AlterKind
{
    DropDatabase,
    DropSchema,
    DropTable,
    TruncateTable,
    AddColumn,
    DropColumn,
    RenameColumn
}

/// <summary>
/// Backs the DROP/ALTER confirmation dialog — the destructive-and-alter counterpart to
/// <see cref="CreateObjectDialogViewModel"/>. Unlike DDL Create, the SQL here is built entirely
/// host-side (<see cref="AlterStatementBuilder"/>, no SDK member) since the syntax needed is close
/// enough across engines; only <see cref="AlterKind.RenameColumn"/> on SQL Server branches internally
/// on the provider id (T-SQL has no ALTER-based rename at all).
/// </summary>
public partial class AlterObjectDialogViewModel : ViewModelBase
{
    private IDbProvider _provider = null!;
    private ISqlDialect _dialect = null!;
    private string _providerId = string.Empty;
    private string? _database;
    private string? _schema;
    private string _target = string.Empty;
    private bool _isView;
    private string _existingColumn = string.Empty;

    [ObservableProperty]
    private string _newColumnName = string.Empty;

    [ObservableProperty]
    private string _newColumnType = string.Empty;

    [ObservableProperty]
    private bool _newColumnNullable = true;

    [ObservableProperty]
    private string _sqlPreview = string.Empty;

    public AlterObjectDialogViewModel(ILocalizer localizer)
    {
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public AlterKind Kind { get; private set; }

    /// <summary>The object being dropped/altered, for the confirmation message (e.g. a table name).</summary>
    public string ObjectLabel { get; private set; } = string.Empty;

    public IReadOnlyList<string> ColumnTypes { get; private set; } = [];

    public bool IsAddColumn => Kind == AlterKind.AddColumn;

    public bool IsRenameColumn => Kind == AlterKind.RenameColumn;

    /// <summary>Drives the dialog's warning styling — every kind here except adding a column removes
    /// something and can't be undone through the app.</summary>
    public bool IsDestructive => Kind != AlterKind.AddColumn;

    public string DialogTitle => Kind switch
    {
        AlterKind.DropDatabase => Loc["DropDatabase"],
        AlterKind.DropSchema => Loc["DropSchema"],
        AlterKind.DropTable => Loc["DropTable"],
        AlterKind.TruncateTable => Loc["TruncateTable"],
        AlterKind.AddColumn => Loc["AddColumnTitle"],
        AlterKind.DropColumn => Loc["DropColumn"],
        _ => Loc["RenameColumn"]
    };

    public bool CanConfirm => Kind switch
    {
        AlterKind.AddColumn => !string.IsNullOrWhiteSpace(NewColumnName) && !string.IsNullOrWhiteSpace(NewColumnType),
        AlterKind.RenameColumn => !string.IsNullOrWhiteSpace(NewColumnName) && NewColumnName != _existingColumn,
        _ => true
    };

    /// <summary>Reset the dialog for a specific drop/alter action — same DI-factory + Configure
    /// pattern as <see cref="CreateObjectDialogViewModel"/> (a per-invocation VM can't take
    /// constructor args from a zero-arg factory delegate).</summary>
    public void Configure(
        AlterKind kind, IDbProvider provider, string providerId, ISqlDialect dialect, IReadOnlyList<string> columnTypes,
        string objectLabel, string? database, string? schema, string target, bool isView = false, string? existingColumn = null)
    {
        Kind = kind;
        _provider = provider;
        _providerId = providerId;
        _database = database;
        _dialect = dialect;
        ColumnTypes = columnTypes;
        ObjectLabel = objectLabel;
        _schema = schema;
        _target = target;
        _isView = isView;
        _existingColumn = existingColumn ?? string.Empty;

        NewColumnName = kind == AlterKind.RenameColumn ? _existingColumn : string.Empty;
        NewColumnType = columnTypes.FirstOrDefault() ?? string.Empty;
        NewColumnNullable = true;

        OnPropertyChanged(nameof(IsAddColumn));
        OnPropertyChanged(nameof(IsRenameColumn));
        OnPropertyChanged(nameof(IsDestructive));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(ColumnTypes));
        RefreshPreview();
    }

    partial void OnNewColumnNameChanged(string value) => RefreshPreview();

    partial void OnNewColumnTypeChanged(string value) => RefreshPreview();

    partial void OnNewColumnNullableChanged(bool value) => RefreshPreview();

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(CanConfirm));

        if (Kind == AlterKind.AddColumn && string.IsNullOrWhiteSpace(NewColumnName))
        {
            SqlPreview = string.Empty;
            return;
        }

        try
        {
            // Give the provider first refusal on the statement (a non-SQL engine returns its own, e.g. a
            // MongoDB db.coll.drop()); null falls back to the host's SQL builder, unchanged for SQL engines.
            var custom = _provider.BuildAlterStatement(BuildSpec());
            SqlPreview = custom?.Text ?? Kind switch
            {
                AlterKind.DropDatabase => AlterStatementBuilder.DropDatabase(_dialect, _target),
                AlterKind.DropSchema => AlterStatementBuilder.DropSchema(_dialect, _target),
                AlterKind.DropTable => AlterStatementBuilder.DropTable(_dialect, _schema, _target, _isView),
                AlterKind.TruncateTable => AlterStatementBuilder.Truncate(_providerId, _dialect, _schema, _target),
                AlterKind.AddColumn => AlterStatementBuilder.AddColumn(_dialect, _schema, _target, NewColumnName, NewColumnType, NewColumnNullable),
                AlterKind.DropColumn => AlterStatementBuilder.DropColumn(_dialect, _schema, _target, _existingColumn),
                AlterKind.RenameColumn => AlterStatementBuilder.RenameColumn(_providerId, _dialect, _schema, _target, _existingColumn, NewColumnName),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            SqlPreview = $"-- {ex.Message}";
        }
    }

    // The current dialog state as the SDK's provider-facing spec (for BuildAlterStatement).
    private AlterSpec BuildSpec() => new(
        Action: Kind switch
        {
            AlterKind.DropDatabase => AlterAction.DropDatabase,
            AlterKind.DropSchema => AlterAction.DropSchema,
            AlterKind.DropTable => AlterAction.DropTable,
            AlterKind.TruncateTable => AlterAction.TruncateTable,
            AlterKind.AddColumn => AlterAction.AddColumn,
            AlterKind.DropColumn => AlterAction.DropColumn,
            _ => AlterAction.RenameColumn
        },
        Database: _database,
        Schema: _schema,
        Target: _target,
        IsView: _isView,
        Column: string.IsNullOrEmpty(_existingColumn) ? null : _existingColumn,
        NewName: string.IsNullOrWhiteSpace(NewColumnName) ? null : NewColumnName,
        NewType: string.IsNullOrWhiteSpace(NewColumnType) ? null : NewColumnType,
        Nullable: NewColumnNullable);
}
