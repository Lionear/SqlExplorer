using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Mcp;

namespace SqlExplorer.Core.Tests.Mcp;

// The classifier is the MCP write-guard's security boundary. These tests pin the per-mode allow-table,
// with the SE-155 addition of Sandbox (Read + DML + DDL) — which must still refuse multi-statement and
// unknown payloads so a schema-building sandbox can't be turned into an injection vector.
public class McpSqlClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    public void Read_is_allowed_from_ReadOnly_upwards(string sql)
    {
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.None));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadOnly));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadWrite));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.Sandbox));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    public void Dml_needs_ReadWrite_or_higher(string sql)
    {
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadOnly));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadWrite));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.Sandbox));
    }

    [Theory]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    public void Ddl_is_allowed_only_in_Sandbox(string sql)
    {
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadOnly));
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadWrite));
        Assert.True(McpSqlClassifier.IsAllowed(sql, AiAccessMode.Sandbox));
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE t")]      // multi-statement
    [InlineData("GORP FROM t")]                  // unknown leading keyword
    public void Multi_and_unknown_are_rejected_even_in_Sandbox(string sql)
    {
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.Sandbox));
        Assert.False(McpSqlClassifier.IsAllowed(sql, AiAccessMode.ReadWrite));
    }

    [Fact]
    public void None_permits_nothing()
    {
        Assert.False(McpSqlClassifier.IsAllowed("SELECT 1", AiAccessMode.None));
        Assert.False(McpSqlClassifier.IsAllowed("CREATE TABLE t (id int)", AiAccessMode.None));
    }
}
