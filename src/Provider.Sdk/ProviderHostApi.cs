namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Versioning contract between the host and provider plugins. A plugin manifest
/// declares the host API version it was built against; the loader refuses a plugin
/// whose version this host cannot satisfy. Bump <see cref="Version"/> on a breaking
/// change to <see cref="IDbProvider"/> or the shared DTOs.
/// </summary>
public static class ProviderHostApi
{
    // v2 (2026-07-11): added ConnectionFields + BuildConnectionString to IDbProvider.
    // v3 (2026-07-12): replaced eager IntrospectSchemaAsync with lazy GetChildNodesAsync (DBeaver tree).
    // v4 (2026-07-12): added IDbProvider.Icon (ProviderIcon: glyph and/or image).
    // v5 (2026-07-12): added ResultColumn edit metadata (Base*/IsKey/…) + IDbProvider.ExecuteBatchAsync
    //                  (editable resultset save-flow, Notes §8).
    // v6 (2026-07-12): added IDbProvider.DisplayName (human-friendly provider label).
    // v7 (2026-07-12): ISqlDialect.Paginate gained an optional orderBy (server-side browse sort).
    // v8 (2026-07-12): added DbNodeKind SchemaFolder/IndexFolder/SequenceFolder/Index/Sequence/Group
    //                  (richer schema tree: schemas grouping, indexes, sequences, cosmetic folders).
    // v9 (2026-07-12): added DbNodeKind Object (generic provider-defined leaf: users/roles/logins/jobs).
    public const int Version = 9;

    /// <summary>True when this host can load a plugin built for <paramref name="pluginVersion"/>.</summary>
    public static bool IsCompatible(int pluginVersion) => pluginVersion == Version;
}
