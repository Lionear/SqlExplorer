namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Versioning contract between the host and provider plugins. A plugin manifest
/// declares the host API version it was built against; the loader refuses a plugin
/// whose version this host cannot satisfy. Bump <see cref="Version"/> on a breaking
/// change to <see cref="IDbProvider"/> or the shared DTOs.
/// </summary>
public static class ProviderHostApi
{
    // v2 (2026-07-11): added ConnectionFields + BuildConnectionString to IDbProvider.
    // v3 (2026-07-12): replaced eager IntrospectSchemaAsync with lazy GetChildNodesAsync (DBeaver tree).
    // v4 (2026-07-12): added IDbProvider.Icon (ProviderIcon: glyph and/or image).
    // v5 (2026-07-12): added ResultColumn edit metadata (Base*/IsKey/…) + IDbProvider.ExecuteBatchAsync
    //                  (editable resultset save-flow, Notes §8).
    // v6 (2026-07-12): added IDbProvider.DisplayName (human-friendly provider label).
    // v7 (2026-07-12): ISqlDialect.Paginate gained an optional orderBy (server-side browse sort).
    // v8 (2026-07-12): added DbNodeKind SchemaFolder/IndexFolder/SequenceFolder/Index/Sequence/Group
    //                  (richer schema tree: schemas grouping, indexes, sequences, cosmetic folders).
    // v9 (2026-07-12): added DbNodeKind Object (generic provider-defined leaf: users/roles/logins/jobs).
    // v10 (2026-07-12): removed the DatabaseKind enum. Provider identity is now the manifest 'id'
    //                   string (loader-attached); dropped IDbProvider.Kind, ISqlDialect.Kind and
    //                   ConnectionProfile.Kind. Open engine set — no central enum to extend.
    // v11 (2026-07-12): added ConnectionProfile.Database (execute-time catalog context; fixes BUG-1
    //                   where MSSQL browse/generate ran against the default catalog, not the tree's db)
    //                   and ISqlDialect.QualifyName (dialect-driven qualified names for generated SQL;
    //                   SQL Server three-part [db].[schema].[table] so a query tab hits the right db).
    // v12 (2026-07-12): added Capabilities.cs (DbObjectKind/CreateCapability/CreateObjectSpec/
    //                   NewColumnSpec) + IDbProvider.CreateCapabilities/ColumnTypes/
    //                   BuildCreateStatement/ExecuteDdlAsync (DDL Create: databases/schemas/tables from
    //                   the tree) and IDbProvider.GetDatabasesAsync (query-tab database switcher).
    //                   Postgres and MySql now also honour ConnectionProfile.Database at execute time
    //                   (previously MSSQL-only, see v11) so the switcher works on every engine.
    // v13 (2026-07-12): added NewColumnSpec.AutoIncrement — genuinely provider-specific rendering
    //                   (Postgres GENERATED ALWAYS AS IDENTITY, MySQL AUTO_INCREMENT, SQL Server
    //                   IDENTITY(1,1), SQLite's INTEGER PRIMARY KEY AUTOINCREMENT column-shape) so it
    //                   belongs in BuildCreateStatement, unlike DROP/ALTER which stayed host-only.
    // v14 (2026-07-12): added DbNodeKind ForeignKeyFolder/ForeignKey (FK-introspection in the tree,
    //                   an enabler for a future ER-diagram) and IDbProvider.ExecuteScriptAsync
    //                   (raw SQL text -> every result set via NextResult, powers "Run"/"Run at cursor"
    //                   and multi-resultset scripts) + ExplainAsync (per-engine EXPLAIN as a QueryResult).
    // v15 (2026-07-13): ConnectionField gained Group/Advanced metadata + ConnectionFieldType.Choice
    //                   (dropdown, values in ConnectionField.Choices) so providers can declare a rich,
    //                   grouped "Advanced" connection section the host renders (Notes §4.4, FR-2/4/4b).
    //                   MSSQL no longer hardcodes TrustServerCertificate — it is a field now (FR-3).
    //                   Also IDbProvider.ParseConnectionString (default null) — inverse of
    //                   BuildConnectionString, prefills the dialog from a pasted string (FR-1).
    // v16 (2026-07-14): added DbNodeKind ProcedureFolder/Procedure/FunctionFolder/Function/TriggerFolder/
    //                   Trigger (Programmability in the tree) + RoutineParameter and IDbProvider
    //                   .GetObjectDefinitionAsync (default null) / GetRoutineParametersAsync (default [])
    //                   / BuildCallStatement (default throws) — View Definition opens in an editable tab,
    //                   Execute… generates a call script (OUT params captured in a trailing SELECT) the
    //                   user runs. Roadmap Fase 4: browse/execute procedures/functions/triggers.
    // v17 (2026-07-14): added DbTreeNode.Count (int?) — grouping folders (Tables/Views/Procedures/…) carry
    //                   their child count, shown inline as "Tables (22)" without expanding. Additive.
    // v18 (2026-07-14): added Activity Monitor to IDbProvider (all default not-supported):
    //                   SupportsActivityMonitor + GetActiveSessionsAsync (ActiveSessionSnapshot: sessions
    //                   QueryResult + current session id for the own-session guard) + SessionIdColumn +
    //                   KillSessionAsync (hard) + SupportsCancelQuery + CancelQueryAsync (soft). Live
    //                   sessions/queries screen per connection, third DocumentMode.Monitor reusing the
    //                   grid/tab infra. Postgres/MySQL support Cancel (pg_cancel_backend / KILL QUERY),
    //                   MSSQL is Kill-only, SQLite unsupported. Roadmap "Activity Monitor" (plan #2 of 4).
    public const int Version = 18;

    /// <summary>True when this host can load a plugin built for <paramref name="pluginVersion"/>.</summary>
    public static bool IsCompatible(int pluginVersion) => pluginVersion == Version;
}
