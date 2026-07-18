using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Formatting;
using ScriptDomCasing = Microsoft.SqlServer.TransactSql.ScriptDom.KeywordCasing;
using SdkCasing = SqlExplorer.Sdk.Formatting.KeywordCasing;

namespace SqlExplorer.Providers.MsSql;

/// <summary>
/// A T-SQL formatter built on Microsoft's official ScriptDom parser (SE-148 phase 2). It parses the
/// statement into an AST and re-emits it with <see cref="Sql160ScriptGenerator"/>, so it understands
/// real T-SQL structure instead of re-flowing tokens the way the host's generic formatter does.
/// Unparseable input (a partial snippet, or a statement the parser rejects) is returned unchanged, so
/// the Format command never mangles or loses the user's text.
/// </summary>
public sealed class MsSqlFormatter : ISqlFormatter
{
    public string Format(string sql, ISqlDialect dialect, SqlFormatOptions options)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (fragment is null || errors is { Count: > 0 })
        {
            return sql;
        }

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            // ScriptDom has no "preserve" casing; Uppercase is the sensible default for the other two.
            KeywordCasing = options.KeywordCasing == SdkCasing.Lower
                ? ScriptDomCasing.Lowercase
                : ScriptDomCasing.Uppercase,
            IndentationSize = options.IndentSize > 0 ? options.IndentSize : SqlFormatOptions.Default.IndentSize,
            AlignClauseBodies = true,
            IncludeSemicolons = false,
            MultilineSelectElementsList = true,
            MultilineWherePredicatesList = true,
            NewLineBeforeFromClause = true,
            NewLineBeforeWhereClause = true,
            NewLineBeforeGroupByClause = true,
            NewLineBeforeHavingClause = true,
            NewLineBeforeOrderByClause = true,
            NewLineBeforeJoinClause = true,
        });

        generator.GenerateScript(fragment, out var formatted);
        return formatted.TrimEnd();
    }
}
