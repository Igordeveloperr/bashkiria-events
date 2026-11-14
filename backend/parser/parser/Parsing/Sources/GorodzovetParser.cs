using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Configuration;

namespace Bashkiria.Events.Parser.Parsing.Sources;

public sealed partial class GorodzovetParser(StructuredEventParser structured) : ISourceEventParser
{
    public string Kind => SourceKinds.Gorodzovet;
    public string ListingLinkSelector => ".eventsWrapper a[href]";

    public Uri? NextListingPage(string html, Uri pageUrl, int nextPage)
    {
        using var document = new HtmlParser().ParseDocument(html);
        return HtmlFields.Link(document.QuerySelector("a[rel='next']")?.GetAttribute("href"), pageUrl);
    }

    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, SourceOptions source)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var records = StructuredDataReader.ReadEvents(document);
        foreach (var record in records)
        {
            // This site puts the city in addressRegion. Correct only this known case;
            // an explicitly different region is still rejected by the shared parser.
            foreach (var place in StructuredDataReader.Values(record["location"]).OfType<JsonObject>())
                if (place["address"] is JsonObject address && source.City is not null &&
                    string.Equals(StructuredDataReader.Text(address["addressLocality"]), source.City, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(StructuredDataReader.Text(address["addressRegion"]), source.City, StringComparison.OrdinalIgnoreCase))
                    address.Remove("addressRegion");
            foreach (var offer in StructuredDataReader.Values(record["offers"]).OfType<JsonObject>())
                if (offer["priceSpecification"] is JsonObject specification)
                {
                    offer["lowPrice"] ??= specification["minPrice"]?.DeepClone();
                    offer["highPrice"] ??= specification["maxPrice"]?.DeepClone();
                }
        }
        var seeds = structured.ParseRecords(records, pageUrl, source)
            .Where(item => item.SourceUrl == Common.SourceUrls.Canonical(pageUrl)).ToArray();
        if (seeds.Length == 0) return [];
        var title = HtmlFields.Text(document.QuerySelector("h1.eventTitle__text, h1"));
        var ticket = HtmlFields.Link(document.QuerySelector(".recordlink[data-link]")?.GetAttribute("data-link"), pageUrl);
        var result = new List<EventCandidate>();
        foreach (var seed in seeds)
        {
            var sessions = new List<(DateOnly Date, TimeOnly? Start, TimeOnly? End, string Venue)>();
            foreach (var row in document.QuerySelectorAll(".eventPreview__info .schedule"))
            {
                var dateLink = row.QuerySelector("[data-link*='/day']")?.GetAttribute("data-link") ?? "";
                var dateMatch = SessionDate().Match(dateLink);
                if (!DateOnly.TryParseExact(dateMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                var times = SessionTime().Match(HtmlFields.Text(row.FirstElementChild));
                sessions.Add((date, HtmlFields.Time(times.Groups[1].Value), HtmlFields.Time(times.Groups[2].Value),
                    HtmlFields.Text(row.QuerySelector(".seance-venue-name"))));
            }
            var groups = sessions.GroupBy(item => (item.Date, item.Start)).ToArray();
            if (groups.Length == 0)
            {
                result.Add(seed with
                {
                    Title = title.Length > 0 ? title : seed.Title,
                    OrgLink = ticket ?? seed.OrgLink,
                    Issues = [.. seed.Issues, new("schedule_unconfirmed", "Расписание агрегатора не подтверждено отдельными сеансами.")]
                });
                continue;
            }
            foreach (var group in groups)
            {
                var issues = seed.Issues.ToList();
                var ends = group.Select(item => item.End).Where(item => item.HasValue).Distinct().ToArray();
                if (ends.Length > 1) issues.Add(new("session_end_conflict", "Для сеанса указаны разные варианты окончания."));
                if (group.Any(item => item.Venue.Length > 0 && !string.Equals(item.Venue, seed.Venue, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new("session_venue_conflict", "Площадка сеанса отличается от площадки карточки агрегатора."));
                if (groups.Length > 1)
                    issues.Add(new("session_identity_unconfirmed", "Агрегатор не предоставляет устойчивый ID каждого сеанса."));
                var end = ends.Length == 1 ? ends[0] : null;
                result.Add(seed with
                {
                    Title = title.Length > 0 ? title : seed.Title,
                    ExternalId = groups.Length == 1 ? seed.ExternalId :
                        pageUrl.AbsoluteUri + "|" + group.Key.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + group.Key.Start?.ToString("HH:mm", CultureInfo.InvariantCulture),
                    StartDate = group.Key.Date, StartTime = group.Key.Start,
                    EndDate = end.HasValue ? group.Key.Date : null, EndTime = end,
                    OrgLink = ticket ?? seed.OrgLink,
                    AgeLimit = seed.AgeLimit ?? EventFields.Age(HtmlFields.Text(document.QuerySelector(".eventTitle")) + " " + HtmlFields.Text(document.QuerySelector(".eventPreview__info"))),
                    Issues = issues
                });
            }
        }
        return result.DistinctBy(item => (item.SourceUrl, item.StartDate, item.StartTime)).ToArray();
    }

    [GeneratedRegex(@"/day(\d{4}-\d{2}-\d{2})/?$")]
    private static partial Regex SessionDate();
    [GeneratedRegex(@"(?<!\d)(\d{1,2}:\d{2})(?:\s*[–—-]\s*(\d{1,2}:\d{2}))?")]
    private static partial Regex SessionTime();
}
