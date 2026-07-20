using System.Linq;
using SqlExplorer.Providers.MsSql;
using SqlExplorer.Providers.MySql;
using SqlExplorer.Providers.Postgres;
using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Tests.Completion;

// The function catalogue is a dialect seam (SE-149 phase 2): each SQL dialect ships its own built-in list,
// and a dialect that declares none inherits the empty default-interface member.
public class DialectFunctionsTests
{
    [Fact]
    public void Bundled_dialects_expose_their_function_catalogue()
    {
        foreach (ISqlDialect dialect in new ISqlDialect[] { new PostgresDialect(), new MySqlDialect(), new MsSqlDialect() })
        {
            Assert.NotEmpty(dialect.Functions);
            Assert.Contains(dialect.Functions, f => f.Name.Equals("coalesce", System.StringComparison.OrdinalIgnoreCase));
            Assert.All(dialect.Functions, f => Assert.False(string.IsNullOrWhiteSpace(f.Signature)));
        }
    }

    [Fact] // Dialect-specific: MySQL has ifnull, SQL Server has isnull — they must not be interchanged.
    public void Dialects_carry_their_own_function_names()
    {
        Assert.Contains(new MySqlDialect().Functions, f => f.Name == "ifnull");
        Assert.DoesNotContain(new MySqlDialect().Functions, f => f.Name == "isnull");

        Assert.Contains(new MsSqlDialect().Functions, f => f.Name == "isnull");
        Assert.Contains(new MsSqlDialect().Functions, f => f.Name == "getdate");
    }

    [Fact] // A dialect that declares no functions inherits the empty default-interface member.
    public void A_dialect_without_functions_defaults_to_empty()
    {
        ISqlDialect bare = new BareDialect();
        Assert.Empty(bare.Functions);
    }

    // Implements only the required members, leaving Functions to its default.
    private sealed class BareDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords { get; } = new HashSet<string>();
        public string QuoteIdentifier(string identifier) => identifier;
        public string QualifyName(string? database, string? schema, string table) => table;
        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }
}
