using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;

namespace Bashkiria.Events.Parser.Parsing.Sources;

public sealed class BashgfParser : ISourceEventParser
{
    public string Kind => SourceKinds.Bashgf;

    public Uri? NextListingPage(string html, Uri pageUrl, int nextPage)
    {
        using var document = new HtmlParser().ParseDocument(html);
        return HtmlFields.Link(document.QuerySelector("a.next.page-numbers, a[rel='next']")?.GetAttribute("href"), pageUrl);
    }

    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, SourceOptions source)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var article = document.QuerySelector("article.type-event");
        if (article is null) return [];
        var title = HtmlFields.Text(article.QuerySelector(".entry-title"));
        if (title.Length == 0) return [];
        var start = HtmlFields.Timestamp(article.QuerySelector(".dtstart[title]")?.GetAttribute("title"));
        var end = HtmlFields.Timestamp(article.QuerySelector(".dtend[title]")?.GetAttribute("title"));
        // The site's template repeats the start as the end when duration is unknown.
        if (end == start) end = null;
        var description = HtmlFields.Text(article.QuerySelector(".my-poster-content"));
        var price = EventFields.Price(null, HtmlFields.Text(article.QuerySelector(".event-tickets")));
        var hall = HtmlFields.Text(article.QuerySelector(".event-category a"));
        return [new EventCandidate
        {
            Title = title,
            SourceUrl = pageUrl,
            ExternalId = string.IsNullOrWhiteSpace(article.Id) ? null : article.Id,
            StartDate = start.HasValue ? DateOnly.FromDateTime(start.Value) : null,
            StartTime = start.HasValue ? TimeOnly.FromDateTime(start.Value) : null,
            EndDate = end.HasValue ? DateOnly.FromDateTime(end.Value) : null,
            EndTime = end.HasValue ? TimeOnly.FromDateTime(end.Value) : null,
            City = source.City,
            Venue = source.Venue,
            Address = source.Address,
            Description = description,
            PriceMin = price.Min, PriceMax = price.Max, IsFree = price.Free,
            AgeLimit = EventFields.Age(HtmlFields.Text(article.QuerySelector(".my-posters-content")) + " " + description),
            Category = EventFields.Category(title + " " + description),
            SourceStatus = SourceEventStatus.Unknown,
            OrgLink = HtmlFields.Link(article.QuerySelector("a.widget-tr[href]")?.GetAttribute("href"), pageUrl),
            ImageUrl = HtmlFields.Link(article.QuerySelector(".my-posters-photo img")?.GetAttribute("src"), pageUrl),
            Tags = hall.Length == 0 ? [] : [hall]
        }];
    }
}
