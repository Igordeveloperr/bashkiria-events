using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;

namespace Bashkiria.Events.Parser.Api;

public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var preview = app.MapGroup("/parser/preview").AddEndpointFilter<ParserApiKeyFilter>().RequireRateLimiting("parser-api");
        preview.MapGet("/gkz/collect", async (GkzCollector collector, CancellationToken token) =>
            Results.Ok(await collector.CollectAsync(token)));
        preview.MapGet("/gkz/links", async (GkzClient client, GkzListingParser parser, CancellationToken token) =>
        {
            var document = await client.GetListingPageAsync(token);
            var links = parser.ParseLinks(document.Content, document.Url);
            return Results.Ok(new { rawDocumentId = document.Id, count = links.Count, links });
        });
        preview.MapGet("/gkz", async (GkzClient client, IEventParser parser, CancellationToken token) =>
        {
            var url = new Uri("https://gkzbashkortostan.ru/events/yubilejnyj-gala-konczert-studii-diva/");
            var document = await client.GetEventPageAsync(url, token);
            return Results.Ok(parser.Parse(document.Content, url).Select(item => item with { RawDocumentId = document.Id }));
        });
        preview.MapPost("", (ParsePreviewRequest request, IEventParser parser) =>
        {
            if (string.IsNullOrWhiteSpace(request.Html) || request.PageUrl is null || !request.PageUrl.IsAbsoluteUri ||
                !Common.SourceUrls.IsAllowed(request.PageUrl, GkzClient.ListingUrl) ||
                !request.PageUrl.AbsolutePath.StartsWith("/events/", StringComparison.Ordinal))
                return ApiErrors.Problem(400, "Укажите HTML и HTTPS-адрес карточки ГКЗ.");
            return Results.Ok(parser.Parse(request.Html, request.PageUrl));
        });
    }
}
