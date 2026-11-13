using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Bashkiria.Events.Parser.Storage;

namespace Bashkiria.Events.Parser.Collection;

public sealed class GkzCollector
{
    private const int MaxPages = 3;

    private readonly GkzClient _client;
    private readonly GkzListingParser _listingParser;
    private readonly IEventParser _eventParser;
    private readonly ILogger<GkzCollector> _logger;

    public GkzCollector(
        GkzClient client,
        GkzListingParser listingParser,
        IEventParser eventParser,
        ILogger<GkzCollector> logger)
    {
        _client = client;
        _listingParser = listingParser;
        _eventParser = eventParser;
        _logger = logger;
    }

    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Without a listing there is no collection to continue. Let the caller report failure.
        var listing = await _client.GetListingPageAsync(cancellationToken);
        var links = _listingParser.ParseLinks(listing.Content, listing.Url);
        var items = new List<EventCandidate>();
        var errors = new List<CollectionError>();
        var processedPages = 0;
        var downloadedPages = 0;
        var parsedPages = 0;

        foreach (var pageUrl in links.Take(MaxPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedPages++;

            RawDocument document;
            try
            {
                document = await _client.GetEventPageAsync(pageUrl, cancellationToken);
                downloadedPages++;
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Failed to download GKZ card {PageUrl}", pageUrl);
                errors.Add(new(pageUrl, "download_failed", "Не удалось загрузить карточку."));
                continue;
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "GKZ card download timed out {PageUrl}", pageUrl);
                errors.Add(new(pageUrl, "download_timeout", "Источник не ответил вовремя."));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<EventCandidate> candidates;
            try
            {
                candidates = _eventParser.Parse(document.Content, document.Url);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Failed to parse GKZ card {PageUrl}", pageUrl);
                errors.Add(new(pageUrl, "parse_failed", "Не удалось разобрать карточку."));
                continue;
            }

            if (candidates.Count == 0)
            {
                errors.Add(new(pageUrl, "no_candidates", "В карточке не найдено событий."));
                continue;
            }

            parsedPages++;
            items.AddRange(candidates.Select(item => item with { RawDocumentId = document.Id }));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new CollectionResult
        {
            SourceId = _eventParser.SourceId,
            ListingDocumentId = listing.Id,
            DiscoveredPages = links.Count,
            ProcessedPages = processedPages,
            DownloadedPages = downloadedPages,
            ParsedPages = parsedPages,
            Items = items.ToArray(),
            Errors = errors.ToArray()
        };
    }
}
