namespace Lionear.SqlExplorer.Sdk.Routines;

/// <summary>
/// One parameter of a stored procedure or function, as introspected for the routine "Execute…" flow.
/// <see cref="IsOutput"/> covers both OUT/INOUT parameters and a procedure's return value: a provider
/// that has a return-value concept (e.g. a SQL Server procedure) surfaces it as a synthetic output
/// parameter here rather than the SDK carrying a separate field. The host renders IN parameters as
/// editable inputs and OUT/return rows as disabled "(output)" rows — the values come back in the
/// capture SELECT that <see cref="IDbProvider.BuildCallStatement"/> appends to the generated script.
/// </summary>
public sealed record RoutineParameter(string Name, string Type, bool IsOutput, string? Default);
