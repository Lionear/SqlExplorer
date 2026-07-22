using SqlExplorer.Tools.SchemaDiff;

namespace SqlExplorer.Tools.SchemaDiff.Tests;

public class AlterScriptWriterTests
{
    private static string Script(string providerId, params SchemaChange[] changes) =>
        new AlterScriptWriter(SqlDialect.For(providerId)).Script(changes);

    private static readonly TableDef Orders = new(
        "public", "orders",
        [new ColumnDef("id", "integer", false, null, 1), new ColumnDef("user_id", "integer", true, null, 2)],
        new PrimaryKeyDef("pk_orders", ["id"]),
        [], [], []);

    [Fact]
    public void Postgres_add_column_quotes_with_double_quotes_and_uses_add_column()
    {
        var sql = Script("postgres", new AddColumn(Orders, new ColumnDef("total", "numeric(10,2)", false, "0", 3)));

        Assert.Equal("""ALTER TABLE "public"."orders" ADD COLUMN "total" numeric(10,2) NOT NULL DEFAULT 0;""", sql);
    }

    [Fact]
    public void SqlServer_add_column_uses_brackets_and_add_not_add_column()
    {
        var sql = Script("sqlserver", new AddColumn(Orders, new ColumnDef("total", "int", true, null, 3)));

        Assert.Equal("ALTER TABLE [public].[orders] ADD [total] int;", sql);
    }

    [Fact]
    public void MySql_alter_column_restates_the_whole_definition_with_backticks()
    {
        var from = new ColumnDef("total", "int", true, null, 3);
        var to = new ColumnDef("total", "bigint", false, null, 3);

        var sql = Script("mysql", new AlterColumn(Orders, from, to));

        Assert.Equal("ALTER TABLE `public`.`orders` MODIFY COLUMN `total` bigint NOT NULL;", sql);
    }

    [Fact]
    public void Postgres_alter_column_emits_one_statement_per_facet_changed()
    {
        var from = new ColumnDef("total", "int", false, null, 3);
        var to = new ColumnDef("total", "bigint", true, "0", 3);

        var lines = new AlterScriptWriter(SqlDialect.For("postgres"))
            .Statements([new AlterColumn(Orders, from, to)]);

        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("TYPE bigint"));
        Assert.Contains(lines, l => l.Contains("DROP NOT NULL"));
        Assert.Contains(lines, l => l.Contains("SET DEFAULT 0"));
    }

    [Fact]
    public void SqlServer_default_change_is_surfaced_as_a_comment_not_silently_dropped()
    {
        var from = new ColumnDef("total", "int", false, null, 3);
        var to = new ColumnDef("total", "int", false, "0", 3);

        var lines = new AlterScriptWriter(SqlDialect.For("sqlserver"))
            .Statements([new AlterColumn(Orders, from, to)]);

        Assert.Contains(lines, l => l.StartsWith("-- NOTE") && l.Contains("default"));
    }

    [Fact]
    public void Create_table_renders_columns_primary_key_and_unique_inline_then_index_after()
    {
        var table = new TableDef(
            "public", "users",
            [new ColumnDef("id", "integer", false, null, 1), new ColumnDef("email", "text", false, null, 2)],
            new PrimaryKeyDef("pk_users", ["id"]),
            [new IndexDef("ix_users_email", false, ["email"])],
            [],
            [new UniqueDef("uq_users_email", ["email"])]);

        var lines = new AlterScriptWriter(SqlDialect.For("postgres"))
            .Statements([new CreateTable(table)]);

        Assert.Equal(2, lines.Count);
        Assert.Contains("CREATE TABLE \"public\".\"users\"", lines[0]);
        Assert.Contains("CONSTRAINT \"pk_users\" PRIMARY KEY (\"id\")", lines[0]);
        Assert.Contains("CONSTRAINT \"uq_users_email\" UNIQUE (\"email\")", lines[0]);
        Assert.Equal("CREATE INDEX \"ix_users_email\" ON \"public\".\"users\" (\"email\");", lines[1]);
    }

    [Fact]
    public void Foreign_key_renders_as_add_constraint_referencing_the_target()
    {
        var fk = new ForeignKeyDef("fk_orders_user", ["user_id"], "public", "users", ["id"]);

        var sql = Script("postgres", new AddForeignKey(Orders, fk));

        Assert.Equal(
            "ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"fk_orders_user\" " +
            "FOREIGN KEY (\"user_id\") REFERENCES \"public\".\"users\" (\"id\");",
            sql);
    }

    [Fact]
    public void Drop_table_and_drop_column_render_per_dialect()
    {
        Assert.Equal("DROP TABLE `public`.`orders`;", Script("mysql", new DropTable(Orders)));
        Assert.Equal(
            "ALTER TABLE [public].[orders] DROP COLUMN [user_id];",
            Script("sqlserver", new DropColumn(Orders, new ColumnDef("user_id", "int", true, null, 2))));
    }

    // --- DROP INDEX: the one DDL whose shape, not just its quoting, differs per engine ---

    private static readonly IndexDef ByCustomer = new("ix_orders_customer", false, ["user_id"]);

    [Fact]
    public void Postgres_drops_an_index_by_schema_qualified_name_without_naming_the_table()
    {
        Assert.Equal("""DROP INDEX "public"."ix_orders_customer";""",
            Script("postgres", new DropIndex(Orders, ByCustomer)));
    }

    [Fact]
    public void MySql_and_SqlServer_need_the_table_to_drop_an_index()
    {
        Assert.Equal("DROP INDEX `ix_orders_customer` ON `public`.`orders`;",
            Script("mysql", new DropIndex(Orders, ByCustomer)));
        Assert.Equal("DROP INDEX [ix_orders_customer] ON [public].[orders];",
            Script("sqlserver", new DropIndex(Orders, ByCustomer)));
    }

    [Fact]
    public void Sqlite_drops_an_index_by_bare_name()
    {
        var table = new TableDef("", "orders", Orders.Columns, Orders.PrimaryKey, [], [], []);

        Assert.Equal("""DROP INDEX "ix_orders_customer";""",
            Script("sqlite", new DropIndex(table, ByCustomer)));
    }

    [Fact]
    public void A_created_table_emits_its_secondary_indexes_after_the_create()
    {
        var table = new TableDef(
            "public", "orders", Orders.Columns, Orders.PrimaryKey,
            [new IndexDef("ix_orders_user", false, ["user_id"])], [], []);

        var sql = Script("postgres", new CreateTable(table));

        Assert.Contains("CREATE TABLE \"public\".\"orders\"", sql);
        Assert.EndsWith("""CREATE INDEX "ix_orders_user" ON "public"."orders" ("user_id");""", sql);
    }

    [Fact]
    public void Postgres_renders_a_sequence_backed_column_as_serial_not_as_a_nextval_default()
    {
        // Copying DEFAULT nextval('audit_id_seq') verbatim produces a CREATE TABLE that fails on the
        // target, because that sequence belongs to the source database.
        var table = new TableDef(
            "public", "audit",
            [new ColumnDef("id", "integer", false, "nextval('audit_id_seq'::regclass)", 1)],
            new PrimaryKeyDef("audit_pkey", ["id"]), [], [], []);

        var sql = Script("postgres", new CreateTable(table));

        Assert.Contains(@"""id"" serial", sql);
        Assert.DoesNotContain("nextval", sql);
    }

    [Fact]
    public void Postgres_maps_a_bigint_sequence_column_to_bigserial()
    {
        var table = new TableDef(
            "public", "audit",
            [new ColumnDef("id", "bigint", false, "nextval('audit_id_seq'::regclass)", 1)],
            null, [], [], []);

        Assert.Contains(@"""id"" bigserial", Script("postgres", new CreateTable(table)));
    }

    [Fact]
    public void MySql_leaves_a_primary_key_unnamed_because_it_calls_every_one_PRIMARY()
    {
        var sql = Script("mysql", new CreateTable(Orders));

        Assert.Contains("PRIMARY KEY (`id`)", sql);
        Assert.DoesNotContain("CONSTRAINT `pk_orders`", sql);
    }

    [Fact]
    public void Sqlite_cannot_alter_constraints_so_the_script_says_so_instead_of_emitting_DDL()
    {
        var table = new TableDef("", "orders", Orders.Columns, Orders.PrimaryKey, [], [], []);
        var fk = new ForeignKeyDef("fk_orders_0", ["user_id"], "", "users", ["id"]);

        var sql = Script("sqlite", new AddForeignKey(table, fk));

        Assert.StartsWith("-- NOTE:", sql);
        Assert.DoesNotContain("ALTER TABLE", sql);
    }
}
