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
}
