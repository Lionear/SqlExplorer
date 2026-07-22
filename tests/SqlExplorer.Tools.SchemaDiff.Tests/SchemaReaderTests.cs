using SqlExplorer.Tools.SchemaDiff;

namespace SqlExplorer.Tools.SchemaDiff.Tests;

/// <summary>
/// The readers' pure halves: catalogue/PRAGMA rows in, model out. Fed with literal rowsets shaped exactly
/// as each engine returns them, so the mapping is covered without a live database — the queries themselves
/// are verified against real engines.
/// </summary>
public class SchemaReaderTests
{
    // --- information_schema (Postgres/MySQL/SQL Server) ---

    private static readonly SqlRows NoRows = SqlRows.Of(["table_schema"]);

    private static SqlRows Columns(params object?[][] rows) => SqlRows.Of(
        ["table_schema", "table_name", "column_name", "ordinal_position", "is_nullable", "column_default",
         "data_type", "character_maximum_length", "numeric_precision", "numeric_scale"],
        rows);

    private static SqlRows Indexes(params object?[][] rows) => SqlRows.Of(
        ["table_schema", "table_name", "index_name", "is_unique", "column_name", "ordinal_position"], rows);

    [Fact]
    public void Index_rows_group_into_one_index_per_name_in_column_order()
    {
        var tables = InformationSchemaReader.BuildTables(
            Columns(["public", "orders", "id", 1, "NO", null, "integer", null, 32, 0],
                    ["public", "orders", "customer_id", 2, "YES", null, "integer", null, 32, 0]),
            NoRows,
            NoRows,
            // Deliberately out of order: the ordinal, not the row order, defines the index's columns.
            Indexes(["public", "orders", "ix_orders_customer", 0, "id", 2],
                    ["public", "orders", "ix_orders_customer", 0, "customer_id", 1]));

        var index = Assert.Single(Assert.Single(tables).Indexes);
        Assert.Equal("ix_orders_customer", index.Name);
        Assert.False(index.Unique);
        Assert.Equal(["customer_id", "id"], index.Columns);
    }

    [Fact]
    public void A_unique_index_is_marked_unique_however_the_engine_spells_the_flag()
    {
        var tables = InformationSchemaReader.BuildTables(
            Columns(["dbo", "users", "email", 1, "NO", null, "nvarchar", 255, null, null]),
            NoRows,
            NoRows,
            // SQL Server hands back a bool (True), Postgres/MySQL a 1.
            Indexes(["dbo", "users", "ux_users_email", "True", "email", 1]));

        Assert.True(Assert.Single(Assert.Single(tables).Indexes).Unique);
    }

    [Fact]
    public void Indexes_on_another_table_are_not_attached()
    {
        var tables = InformationSchemaReader.BuildTables(
            Columns(["public", "orders", "id", 1, "NO", null, "integer", null, 32, 0]),
            NoRows,
            NoRows,
            Indexes(["public", "invoices", "ix_invoices_id", 0, "id", 1]));

        Assert.Empty(Assert.Single(tables).Indexes);
    }

    [Fact]
    public void MySql_drops_the_schema_because_there_it_is_the_database_itself()
    {
        // Reading the same table from two MySQL databases must yield the same key, or every table would
        // diff as "drop and recreate".
        var left = InformationSchemaReader.BuildTables(
            Columns(["probe_left", "orders", "id", 1, "NO", null, "int", null, 10, 0]),
            NoRows, NoRows, Indexes(), schemaIsDatabase: true);
        var right = InformationSchemaReader.BuildTables(
            Columns(["probe_right", "orders", "id", 1, "NO", null, "int", null, 10, 0]),
            NoRows, NoRows, Indexes(), schemaIsDatabase: true);

        Assert.Equal("orders", Assert.Single(left).Key);
        Assert.Equal(Assert.Single(left).Key, Assert.Single(right).Key);
    }

    [Fact]
    public void Postgres_and_SqlServer_keep_their_schema_as_part_of_a_table_identity()
    {
        var tables = InformationSchemaReader.BuildTables(
            Columns(["sales", "orders", "id", 1, "NO", null, "integer", null, 32, 0]),
            NoRows, NoRows, Indexes());

        Assert.Equal("sales.orders", Assert.Single(tables).Key);
    }

    // --- SQLite ---

    private static SqlRows SqliteColumns(params object?[][] rows) => SqlRows.Of(
        ["table_name", "ordinal_position", "column_name", "data_type", "notnull", "column_default", "pk"], rows);

    private static SqlRows SqliteForeignKeys(params object?[][] rows) => SqlRows.Of(
        ["table_name", "fk_id", "ordinal_position", "ref_table", "column_name", "ref_column"], rows);

    private static SqlRows SqliteIndexes(params object?[][] rows) => SqlRows.Of(
        ["table_name", "index_name", "is_unique", "origin", "ordinal_position", "column_name"], rows);

    private static readonly SqlRows NoSqliteFks = SqliteForeignKeys();
    private static readonly SqlRows NoSqliteIndexes = SqliteIndexes();

    [Fact]
    public void Sqlite_columns_carry_their_declared_type_nullability_and_default()
    {
        var tables = SqliteSchemaReader.BuildTables(
            SqliteColumns(["orders", 0, "id", "INTEGER", 1, null, 1],
                          ["orders", 1, "note", "VARCHAR(50)", 0, "'none'", 0]),
            NoSqliteFks, NoSqliteIndexes);

        var table = Assert.Single(tables);
        Assert.Equal(string.Empty, table.Schema);   // SQLite has no schemas
        Assert.Equal("orders", table.Name);

        var id = table.Columns[0];
        Assert.Equal("INTEGER", id.DataType);
        Assert.False(id.Nullable);

        var note = table.Columns[1];
        Assert.Equal("VARCHAR(50)", note.DataType);
        Assert.True(note.Nullable);
        Assert.Equal("'none'", note.Default);
    }

    [Fact]
    public void Sqlite_primary_key_columns_follow_the_pk_ordinal_not_the_column_order()
    {
        var tables = SqliteSchemaReader.BuildTables(
            SqliteColumns(["order_lines", 0, "line_no", "INTEGER", 1, null, 2],
                          ["order_lines", 1, "order_id", "INTEGER", 1, null, 1]),
            NoSqliteFks, NoSqliteIndexes);

        var pk = Assert.Single(tables).PrimaryKey;
        Assert.NotNull(pk);
        Assert.Equal(["order_id", "line_no"], pk.Columns);
    }

    [Fact]
    public void Sqlite_foreign_key_without_a_referenced_column_resolves_to_the_target_primary_key()
    {
        var tables = SqliteSchemaReader.BuildTables(
            SqliteColumns(["customers", 0, "id", "INTEGER", 1, null, 1],
                          ["orders", 0, "customer_id", "INTEGER", 0, null, 0]),
            // PRAGMA reports "to" as NULL for REFERENCES customers (no column list).
            SqliteForeignKeys(["orders", 0, 0, "customers", "customer_id", null]),
            NoSqliteIndexes);

        var orders = tables.Single(t => t.Name == "orders");
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal(["customer_id"], fk.Columns);
        Assert.Equal("customers", fk.RefTable);
        Assert.Equal(["id"], fk.RefColumns);
    }

    [Fact]
    public void Sqlite_index_origin_splits_unique_constraints_from_created_indexes_and_drops_the_pk_index()
    {
        var tables = SqliteSchemaReader.BuildTables(
            SqliteColumns(["users", 0, "id", "INTEGER", 1, null, 1],
                          ["users", 1, "email", "TEXT", 0, null, 0],
                          ["users", 2, "name", "TEXT", 0, null, 0]),
            NoSqliteFks,
            SqliteIndexes(
                ["users", "sqlite_autoindex_users_1", 1, "pk", 0, "id"],
                ["users", "sqlite_autoindex_users_2", 1, "u", 0, "email"],
                ["users", "ix_users_name", 0, "c", 0, "name"]));

        var table = Assert.Single(tables);
        // The auto-index name is replaced by one derived from the columns — see UniqueName.
        Assert.Equal("uq_users_email", Assert.Single(table.Uniques).Name);
        Assert.Equal("ix_users_name", Assert.Single(table.Indexes).Name);
    }

    [Fact]
    public void Sqlite_expression_indexes_are_skipped_rather_than_guessed_at()
    {
        var tables = SqliteSchemaReader.BuildTables(
            SqliteColumns(["users", 0, "name", "TEXT", 0, null, 0]),
            NoSqliteFks,
            // PRAGMA index_info reports no column name for an expression index.
            SqliteIndexes(["users", "ix_users_lower_name", 0, "c", 0, null]));

        Assert.Empty(Assert.Single(tables).Indexes);
    }
}
