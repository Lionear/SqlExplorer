namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// One parameterised SQL statement in a save batch. <see cref="Text"/> uses named
/// placeholders (<c>@p0</c>, <c>@p1</c>, …) that line up with <see cref="Parameters"/>
/// by name; providers bind them and run the batch inside a single transaction
/// (see <see cref="IDbProvider.ExecuteBatchAsync"/>).
/// </summary>
public sealed record SqlStatement(string Text, IReadOnlyList<SqlParam> Parameters);

/// <summary>A single bound parameter. <see cref="Name"/> is the placeholder name without the leading marker (e.g. <c>p0</c>).</summary>
public sealed record SqlParam(string Name, object? Value);
