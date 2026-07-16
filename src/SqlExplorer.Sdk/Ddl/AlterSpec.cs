namespace SqlExplorer.Sdk.Ddl;

/// <summary>The structural/destructive tree action being built (DROP/TRUNCATE/ALTER).</summary>
public enum AlterAction
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
/// The inputs for a DROP/TRUNCATE/ALTER action, handed to <see cref="IDbProvider.BuildAlterStatement"/>
/// so a provider can own that statement (e.g. a MongoDB <c>db.coll.drop()</c>) instead of the host's
/// built-in SQL builder. The host previews the returned statement (the user may edit it) and then runs
/// it via <see cref="IDbProvider.ExecuteDdlAsync"/>. A provider returns null for actions it does not
/// handle, and the host falls back to its own SQL generation — the same "null = not supported"
/// convention as <see cref="IDbProvider.ParseConnectionString"/>.
/// </summary>
/// <param name="Action">Which action to build.</param>
/// <param name="Database">Owning database/catalog, when the node sits under one (null otherwise).</param>
/// <param name="Schema">Owning schema, when the engine has one (null otherwise).</param>
/// <param name="Target">The object the action targets — the table/collection name, or the database/
/// schema name for the DROP DATABASE/SCHEMA actions.</param>
/// <param name="IsView">True when <see cref="Target"/> is a view rather than a table.</param>
/// <param name="Column">Existing column name for <see cref="AlterAction.DropColumn"/>/<see cref="AlterAction.RenameColumn"/>.</param>
/// <param name="NewName">New column name for <see cref="AlterAction.AddColumn"/>/<see cref="AlterAction.RenameColumn"/>.</param>
/// <param name="NewType">New column type for <see cref="AlterAction.AddColumn"/>.</param>
/// <param name="Nullable">Whether an added column allows NULL.</param>
public sealed record AlterSpec(
    AlterAction Action,
    string? Database,
    string? Schema,
    string Target,
    bool IsView = false,
    string? Column = null,
    string? NewName = null,
    string? NewType = null,
    bool Nullable = true);
