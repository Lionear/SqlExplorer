namespace SqlExplorer.Sdk;

/// <summary>
/// One built-in function a dialect exposes to SQL completion (SE-149 phase 2): its <see cref="Name"/> (the
/// text inserted), a human-readable <see cref="Signature"/> shown alongside (e.g. <c>coalesce(value [, ...])</c>)
/// and optional one-line <see cref="Doc"/>. Purely descriptive metadata — the host never calls it.
/// </summary>
public sealed record SqlFunction(string Name, string Signature, string? Doc = null);
