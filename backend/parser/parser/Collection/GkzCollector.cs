using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;

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
        var listingHtml = await _client.GetListingHtmlAsync(cancellationToken);
        var links = _listingParser.ParseLinks(listingHtml, GkzClient.ListingUrl);
        var items = new List<EventCandidate>();
        var errors = new List<CollectionError>();
        var processedPages = 0;
        var downloadedPages = 0;
        var parsedPages = 0;

        foreach (var pageUrl in links.Take(MaxPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedPages++;

            string html;
            try
            {
                html = await _client.GetEventHtmlAsync(pageUrl, cancellationToken);
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
                candidates = _eventParser.Parse(html, pageUrl);
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
            items.AddRange(candidates);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new CollectionResult
        {
            SourceId = _eventParser.SourceId,
            DiscoveredPages = links.Count,
            ProcessedPages = processedPages,
            DownloadedPages = downloadedPages,
            ParsedPages = parsedPages,
            Items = items.ToArray(),
            Errors = errors.ToArray()
        };
    }
}
