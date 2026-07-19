namespace SqlExplorer.Core.Connections;

/// <summary>
/// How much MCP (AI) access a connection grants. The primary opt-in gate for the MCP server: a connection
/// is invisible to any AI client until it is explicitly raised above <see cref="None"/>. Fail-closed —
/// <see cref="None"/> is the default for every connection, existing and new (MCP-Server plan §4, CRIT-1).
/// </summary>
public enum AiAccessMode
{
    /// <summary>No AI access at all — the connection never appears in any MCP response. The default.</summary>
    None,

    /// <summary>SELECT/EXPLAIN only; the MCP host rejects any write/DDL statement before it reaches the driver.</summary>
    ReadOnly,

    /// <summary>DML (INSERT/UPDATE/DELETE) additionally allowed; DDL is still always rejected over MCP.</summary>
    ReadWrite,

    /// <summary>Read + DML + DDL — a throwaway sandbox for the AI to build and test a schema against. Only
    /// ever valid for a <see cref="SavedConnection.IsTransient">transient</see> connection to a loopback host
    /// (enforced at creation, SE-155); it must never be selectable for a persisted connection, whose ceiling
    /// stays <see cref="ReadWrite"/>. Multi-statement / unknown SQL is still rejected.</summary>
    Sandbox
}
