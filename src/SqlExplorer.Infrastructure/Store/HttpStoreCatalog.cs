using SqlExplorer.Core.Store;

namespace SqlExplorer.Infrastructure.Store;

/// <summary>
/// Fetches and merges <c>index.json</c> from every active source: the Discovery feed's stores plus the
/// user's manual index URLs. Discovery sources come first (in feed order), then manual ones; a URL that
/// appears in both is fetched once (as a Discovery source). Each source is fetched independently — one
/// that fails is recorded in <see cref="StoreCatalog.Sources"/> and skipped, never aborting the merge.
/// Entries and bundles are deduped by id with the first source that lists them winning. When a whole
/// fetch reaches no source but a previous one succeeded, the last-good entries/bundles are returned
/// alongside the fresh (failed) source statuses so the UI can show the cached catalog with a retry.
/// </summary>
public sealed class HttpStoreCatalog(
    HttpClient http,
    IDiscoveryService discovery,
    IStoreSourcesStore sources) : IStoreCatalog
{
    private StoreCatalog? _lastGood;

    public async Task<StoreCatalog> FetchAsync(CancellationToken ct)
    {
        var resolved = await ResolveSourcesAsync(ct);

        var entries = new List<CatalogEntry>();
        var bundles = new List<CatalogBundle>();
        var statuses = new List<SourceStatus>();
        var seenEntryIds = new HashSet<string>(StringComparer.Ordinal);
        var seenBundleIds = new HashSet<string>(StringComparer.Ordinal);
        var anyOk = false;

        foreach (var source in resolved)
        {
            try
            {
                StoreUrl.EnsureAllowed(source.Url);
                var json = await http.GetStringAsync(source.Url, ct);
                var index = StoreIndex.Parse(json);

                foreach (var entry in index.Plugins)
                {
                    if (seenEntryIds.Add(entry.Id))
                    {
                        entries.Add(new CatalogEntry(entry, source.Url, source.Name));
                    }
                }

                foreach (var bundle in index.Bundles)
                {
                    if (seenBundleIds.Add(bundle.Id))
                    {
                        bundles.Add(new CatalogBundle(bundle, source.Url, source.Name));
                    }
                }

                statuses.Add(new SourceStatus(source.Url, source.Name, source.IsDiscovery, Ok: true, Error: null, source.IconUrl));
                anyOk = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException)
            {
                statuses.Add(new SourceStatus(source.Url, source.Name, source.IsDiscovery, Ok: false, Error: ex.Message, source.IconUrl));
            }
        }

        // Every source failed but we have a cached catalog: show it with the fresh failure statuses.
        if (!anyOk && _lastGood is { } cached)
        {
            return cached with { Sources = statuses };
        }

        var result = new StoreCatalog(entries, bundles, statuses);
        _lastGood = result;
        return result;
    }

    // Discovery stores first (feed order), then manual URLs not already covered by a Discovery source.
    private async Task<List<ResolvedSource>> ResolveSourcesAsync(CancellationToken ct)
    {
        var resolved = new List<ResolvedSource>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in await discovery.GetStoresAsync(ct))
        {
            if (seenUrls.Add(store.IndexUrl))
            {
                resolved.Add(new ResolvedSource(store.IndexUrl, store.Name, IsDiscovery: true, store.IconUrl));
            }
        }

        foreach (var url in sources.GetManualSources())
        {
            if (seenUrls.Add(url))
            {
                resolved.Add(new ResolvedSource(url, Name: null, IsDiscovery: false, IconUrl: null));
            }
        }

        return resolved;
    }

    private sealed record ResolvedSource(string Url, string? Name, bool IsDiscovery, string? IconUrl);
}
