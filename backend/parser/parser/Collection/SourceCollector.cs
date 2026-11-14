using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Collection;

public sealed class SourceCollector(
    SourceRegistry registry, SourcePageClient client, IEventStore events,
    CandidateNormalizer normalizer, IOptions<ParserOptions> options, ILogger<SourceCollector> logger)
{
    public async Task<CollectionResult> CollectAsync(string sourceId, int maxPages,
        Func<CancellationToken, Task>? heartbeat, CancellationToken cancellationToken)
    {
        var source = registry.Get(sourceId);
        var settings = options.Value;
        if (maxPages is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxPages));
        var links = new HashSet<Uri>();
        var items = new List<EventCandidate>();
        var errors = new List<CollectionError>();
        Guid? listingId = null;
        var processed = 0; var downloaded = 0; var parsed = 0;
        var added = 0; var updated = 0; var unchanged = 0;
        var consecutiveFailures = 0;

        var listingUrl = new Uri(source.ListingUrl);
        var visitedListings = new HashSet<Uri>();
        for (var page = 1; page <= settings.MaxListingPages && visitedListings.Add(listingUrl); page++)
        {
            await BeforeRequestAsync();
            try
            {
                var document = await client.DownloadAsync(source, listingUrl, cancellationToken);
                listingId ??= document.Id;
                var discovered = registry.DiscoverLinks(source, document.Content, listingUrl);
                var newLinks = discovered.Count(url => links.Add(url));
                if (newLinks == 0 || links.Count >= maxPages) break;
                var nextListing = registry.NextListingPage(source, document.Content, document.Url, page + 1);
                if (nextListing is null) break;
                listingUrl = nextListing;
            }
            catch (Exception exception) when (IsDownloadFailure(exception, cancellationToken) && listingId.HasValue)
            {
                logger.LogWarning(exception, "Partial listing failure for source {SourceId}", sourceId);
                errors.Add(new(listingUrl, "listing_failed", "Не удалось загрузить следующую страницу афиши."));
                break;
            }
        }

        if (links.Count == 0)
            errors.Add(new(new Uri(source.ListingUrl), "empty_listing", "В афише не найдено ссылок; требуется проверить разметку источника."));

        foreach (var url in links.Take(maxPages))
        {
            await BeforeRequestAsync();
            processed++;
            RawDocument document;
            try
            {
                document = await client.DownloadAsync(source, url, cancellationToken);
                downloaded++;
                consecutiveFailures = 0;
            }
            catch (Exception exception) when (IsDownloadFailure(exception, cancellationToken))
            {
                logger.LogWarning(exception, "Failed to download {SourceId} card {PageUrl}", sourceId, url);
                errors.Add(new(url, exception is OperationCanceledException ? "download_timeout" : "download_failed", "Не удалось загрузить карточку."));
                if (exception is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone } &&
                    ++consecutiveFailures >= settings.MaxConsecutiveFailures)
                {
                    errors.Add(new(url, "source_unavailable", "Сбор остановлен после последовательных ошибок источника."));
                    break;
                }
                continue;
            }

            IReadOnlyList<EventCandidate> candidates;
            if (heartbeat is not null) await heartbeat(cancellationToken);
            try { candidates = registry.Parse(source, document.Content, document.Url); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to parse {SourceId} card {PageUrl}", sourceId, url);
                errors.Add(new(url, "parse_failed", "Не удалось разобрать карточку."));
                continue;
            }

            if (candidates.Count == 0)
            {
                errors.Add(new(url, "no_candidates", "В карточке не найдено событий."));
                continue;
            }
            parsed++;
            foreach (var candidate in candidates.DistinctBy(item => (item.ExternalId ?? item.SourceUrl.AbsoluteUri, item.StartDate, item.StartTime)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (heartbeat is not null) await heartbeat(cancellationToken);
                var normalized = normalizer.Normalize(sourceId, candidate with { RawDocumentId = document.Id });
                var result = await events.SaveAsync(new(source.Id, source.Priority), candidate with { RawDocumentId = document.Id }, document, cancellationToken);
                if (result.Outcome == "added") added++;
                else if (result.Outcome == "updated") updated++;
                else unchanged++;
                items.Add(normalized.Candidate with { Issues = result.Issues ?? normalized.Candidate.Issues });
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new CollectionResult
        {
            SourceId = sourceId,
            ListingDocumentId = listingId,
            DiscoveredPages = links.Count,
            ProcessedPages = processed,
            DownloadedPages = downloaded,
            ParsedPages = parsed,
            Items = items,
            Errors = errors,
            AddedEvents = added,
            UpdatedEvents = updated,
            UnchangedEvents = unchanged
        };

        async Task BeforeRequestAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heartbeat is not null) await heartbeat(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(settings.RequestDelayMilliseconds), cancellationToken);
        }
    }

    private static bool IsDownloadFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
