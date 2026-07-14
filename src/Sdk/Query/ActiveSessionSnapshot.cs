namespace Lionear.SqlExplorer.Sdk.Query;

/// <summary>
/// One Activity-Monitor refresh: the live sessions as an ordinary <see cref="QueryResult"/> (so the
/// host renders them in its existing grid, columns 1:1 from the provider's own query) plus the id of
/// the connection that produced this snapshot. The host uses <see cref="CurrentSessionId"/> to leave
/// the monitor's own row visible but with Kill/Cancel disabled, so you can't shoot down the very
/// connection that's polling. It must be captured on the same connection that runs the sessions query —
/// a second connection would carry a different session id.
/// </summary>
public sealed record ActiveSessionSnapshot(QueryResult Sessions, string? CurrentSessionId);
