namespace SqlExplorer.Sdk;

/// <summary>
/// Versioning contract between the host and provider plugins. A plugin manifest declares the host API
/// version it was built against; the loader accepts any version in [<see cref="MinimumSupported"/>,
/// <see cref="Version"/>]. Additive changes (new default-interface members, enum values, DTOs) stay
/// binary-compatible — plugins built against an older version keep loading — so they bump only
/// <see cref="Version"/>. A breaking change bumps <see cref="MinimumSupported"/> too.
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
    // v19 (2026-07-14): added user/security management to IDbProvider (all default not-supported):
    //                   CanManageUsers + UserFields (Sdk/Security: UserField/UserFieldType) +
    //                   GetAssignableRolesAsync + BuildCreateUserStatement(values, roles) +
    //                   BuildDropUserStatement + DbNodeKind UserFolder/User, plus the Route-B seam
    //                   ICustomSecurityUi (unused in v1). Create/Drop contained-DB users (MSSQL, per
    //                   Database), cluster login-roles (Postgres) and name@host users (MySQL) from the
    //                   tree, with an optional role checkbox list. SQLite unsupported. Roadmap plan #3 of 4.
    // v20 (2026-07-16): added the non-SQL provider seam to IDbProvider (all default to current behaviour):
    //                   IsSqlBased (false suppresses the host's SQL-scaffold "SQL commands" submenu) +
    //                   BuildNodeQuery (NodeQueryKind — provider-owned "Select top 1000"/SQL-commands query
    //                   text, null = host SQL) + BuildAlterStatement (AlterSpec/AlterAction — provider-owned
    //                   DROP/TRUNCATE, null = host SQL). Lets a document store (MongoDB) generate its own
    //                   shell text instead of SELECT/DROP; SQL providers are unchanged (defaults).
    // v21 (2026-07-16): added DbNodeKind LoginFolder/Login (server-level logins in the tree) and reshaped
    //                   the (still-unimplemented) Route-B seam ICustomSecurityUi to CreateSecurityView(
    //                   SecurityUiContext) + SecurityUiAction, so a provider can own the server-Login flow
    //                   (create/drop, server-role membership, login→user mapping, SQL+Windows auth). Purely
    //                   additive: enum values + an unused interface's shape, so v20 plugins keep loading.
    // v22 (2026-07-16): added Sdk/Editing/ChangeSet.cs (RowChange/CellChange/ChangeSet/WritebackResult) +
    //                   IDbProvider.ApplyChangesAsync (default throws) — the non-SQL half of the
    //                   editable-grid save-flow (SE-114). The host builds a ChangeSet from the same
    //                   Base*/IsKey column metadata SQL providers use for ExecuteBatchAsync, so a non-SQL
    //                   provider (Mongo, later Elastic/Redis) opts into an editable grid by populating that
    //                   metadata and implementing this method instead of generating SQL. Purely additive;
    //                   SQL providers are unchanged (still routed through ExecuteBatchAsync).
    // v23 (2026-07-16): IDbProvider.BuildNodeQuery gained a ConnectionProfile parameter — a provider whose
    //                   node-action text depends on a live lookup (Redis: TYPE key, to pick GET/HGETALL/
    //                   LRANGE/SMEMBERS/ZRANGE for "Select top 1000") had no connection to call with under
    //                   the old signature, and DbNodeRef (Kind+Name, where Name is the displayed tree label)
    //                   has nowhere to smuggle provider-owned metadata through instead. BREAKING for the one
    //                   provider that overrides it (MongoDB; recompiled, ignores the new parameter — its
    //                   browse text never depended on a live call), hence MinimumSupported also moves to 23.
    //                   Also added plugins/Providers.Redis (SE-20): the first non-SQL provider to use
    //                   SE-114's ApplyChangesAsync writeback, scoped to Hash keys (field/value rows map
    //                   cleanly onto Base*/IsKey; List/Set/ZSet/String stay read-only via the grid, mutated
    //                   through the console — see the plugin's README for why).
    public const int Version = 23;

    /// <summary>Oldest plugin ABI this host still loads. Additive bumps (v11→v22 style) keep this fixed;
    /// only a breaking change raises it. Raised to 23 by the v23 BuildNodeQuery signature change above —
    /// pre-v20 versions also added some abstract members (v12/v14), so a full default-implementation audit
    /// would be needed before ever lowering it back down. Previous breaking change: v10 (removed
    /// DatabaseKind).</summary>
    public const int MinimumSupported = 23;

    /// <summary>True when this host can load a plugin built for <paramref name="pluginVersion"/> — any
    /// version in [<see cref="MinimumSupported"/>, <see cref="Version"/>].</summary>
    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
