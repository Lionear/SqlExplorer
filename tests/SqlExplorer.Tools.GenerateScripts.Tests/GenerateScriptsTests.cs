using SqlExplorer.Plugins.Schema;
using SqlExplorer.Tools.GenerateScripts;

namespace SqlExplorer.Tools.GenerateScripts.Tests;

/// <summary>
/// The assembly step: a whole schema in, one runnable script out. The DDL rendering itself belongs to
/// <c>AlterScriptWriter</c> and is tested with Schema Diff; what matters here is <b>order</b> — a
/// whole-database script that emits a foreign key before the table it points at simply doesn't run.
/// </summary>
public class GenerateScriptsTests
{
    private static ColumnDef Col(string name, string type = "integer", int ord = 1, bool identity = false) =>
        new(name, type, Nullable: false, Default: null, Ordinal: ord, IsIdentity: identity);

    private static readonly TableDef Customers = new(
        "public", "customers",
        [Col("id", identity: true), Col("email", "text", 2)],
        new PrimaryKeyDef("pk_customers", ["id"]),
        [new IndexDef("ix_customers_email", Unique: false, ["email"])],
        [],
        [new UniqueDef("uq_customers_email", ["email"])]);

    private static readonly TableDef Orders = new(
        "public", "orders",
        [Col("id", identity: true), Col("customer_id", ord: 2)],
        new PrimaryKeyDef("pk_orders", ["id"]),
        [],
        [new ForeignKeyDef("fk_orders_customer", ["customer_id"], "public", "customers", ["id"])],
        []);

    private static string Script(bool includeIndexes = true, bool dropFirst = false, string engine = "postgres") =>
        GenerateScriptsTool.Build(new SchemaSnapshot([Orders, Customers]), engine, includeIndexes, dropFirst, "-- header");

    [Fact]
    public void Every_table_is_created()
    {
        var sql = Script();
        Assert.Contains("CREATE TABLE \"public\".\"customers\"", sql);
        Assert.Contains("CREATE TABLE \"public\".\"orders\"", sql);
        Assert.StartsWith("-- header", sql);
    }

    [Fact]
    public void Tables_come_out_in_a_stable_order_regardless_of_how_they_were_read()
    {
        // Re-running must produce the same file, or a diff of two runs is noise.
        var sql = Script();
        Assert.True(sql.IndexOf("\"customers\" (", StringComparison.Ordinal)
                    < sql.IndexOf("\"orders\" (", StringComparison.Ordinal),
            "customers should precede orders alphabetically, whatever order the reader returned");
    }

    [Fact]
    public void Foreign_keys_come_after_every_table_exists()
    {
        // The whole point of scripting a database rather than a table: orders references customers, so the
        // constraint cannot be emitted with the table.
        var sql = Script();
        var fk = sql.IndexOf("ADD CONSTRAINT \"fk_orders_customer\"", StringComparison.Ordinal);

        Assert.True(fk > 0, "the foreign key should be scripted");
        Assert.True(fk > sql.IndexOf("CREATE TABLE \"public\".\"orders\"", StringComparison.Ordinal));
        Assert.True(fk > sql.IndexOf("CREATE TABLE \"public\".\"customers\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Primary_key_and_unique_are_inline_and_indexes_follow_their_table()
    {
        var sql = Script();
        Assert.Contains("CONSTRAINT \"pk_customers\" PRIMARY KEY (\"id\")", sql);
        Assert.Contains("CONSTRAINT \"uq_customers_email\" UNIQUE (\"email\")", sql);
        Assert.Contains("CREATE INDEX \"ix_customers_email\" ON \"public\".\"customers\" (\"email\");", sql);
    }

    [Fact]
    public void Tables_only_leaves_out_indexes_and_foreign_keys()
    {
        var sql = Script(includeIndexes: false);

        Assert.Contains("CREATE TABLE \"public\".\"customers\"", sql);
        Assert.DoesNotContain("CREATE INDEX", sql);
        Assert.DoesNotContain("FOREIGN KEY", sql);
        // The primary key and unique constraints are part of the table, not of "indexes".
        Assert.Contains("PRIMARY KEY (\"id\")", sql);
        Assert.Contains("UNIQUE (\"email\")", sql);
    }

    [Fact]
    public void Drops_run_in_reverse_order_so_a_referenced_table_goes_last()
    {
        var sql = Script(dropFirst: true);
        var dropOrders = sql.IndexOf("DROP TABLE \"public\".\"orders\"", StringComparison.Ordinal);
        var dropCustomers = sql.IndexOf("DROP TABLE \"public\".\"customers\"", StringComparison.Ordinal);

        Assert.True(dropOrders > 0 && dropCustomers > 0);
        Assert.True(dropOrders < dropCustomers, "orders references customers, so it must be dropped first");
        Assert.True(dropCustomers < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal),
            "every drop belongs before the first create");
    }

    [Fact]
    public void Auto_numbering_survives_into_the_script()
    {
        Assert.Contains("\"id\" serial", Script());
        Assert.Contains("`id` integer NOT NULL AUTO_INCREMENT", Script(engine: "mysql"));
        Assert.Contains("[id] integer IDENTITY(1,1) NOT NULL", Script(engine: "sqlserver"));
    }

    [Fact]
    public void Each_engine_gets_its_own_quoting()
    {
        Assert.Contains("CREATE TABLE `public`.`orders`", Script(engine: "mysql"));
        Assert.Contains("CREATE TABLE [public].[orders]", Script(engine: "sqlserver"));
    }

    [Fact]
    public void An_empty_schema_still_produces_a_header_and_nothing_else()
    {
        var sql = GenerateScriptsTool.Build(new SchemaSnapshot([]), "postgres", true, false, "-- header");
        Assert.StartsWith("-- header", sql);
        Assert.DoesNotContain("CREATE TABLE", sql);
    }
}
