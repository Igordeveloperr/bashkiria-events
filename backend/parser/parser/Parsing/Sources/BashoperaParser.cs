using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;

namespace Bashkiria.Events.Parser.Parsing.Sources;

public sealed partial class BashoperaParser(TimeProvider clock) : ISourceEventParser
{
    public string Kind => SourceKinds.Bashopera;
    public string ListingLinkSelector => ".item.content .name a[href]";

    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, SourceOptions source)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var card = document.QuerySelector(".b-event");
        var title = HtmlFields.Text(document.QuerySelector("#article-content h1, h1"));
        if (card is null || title.Length == 0) return [];
        var description = HtmlFields.Text(card.QuerySelector(":scope > .text"));
        var age = EventFields.Age(HtmlFields.Text(card.QuerySelector(".info .b-tags")));
        var duration = ReadDuration(HtmlFields.Text(card.QuerySelector(".info .description")));
        var category = pageUrl.AbsolutePath.Contains("/concert/", StringComparison.Ordinal) ? EventCategory.Concert : EventCategory.Theatre;
        var result = new List<EventCandidate>();
        foreach (var session in card.QuerySelectorAll(".affiche .event"))
        {
            var issues = new List<ParsingIssue>();
            var day = HtmlFields.Text(session.QuerySelector(".date .left2"));
            var month = HtmlFields.Text(session.QuerySelector(".date .month"));
            var weekday = HtmlFields.Text(session.QuerySelector(".day"));
            var date = ReadDate(day + " " + month, weekday);
            if (date.HasValue && !ExplicitYear().IsMatch(day + " " + month))
                issues.Add(new("inferred_start_year", "В афише нет года сеанса. Год сопоставлен по дню недели и требует проверки."));
            var time = HtmlFields.Time(HtmlFields.Text(session.QuerySelector(".time span, .time")));
            var ticket = HtmlFields.Link(session.QuerySelector(".buy a[href]")?.GetAttribute("href"), pageUrl);
            var price = EventFields.Price(null, HtmlFields.Text(session.QuerySelector(".price")));
            DateTime? end = date.HasValue && time.HasValue && duration.HasValue
                ? date.Value.ToDateTime(time.Value).AddMinutes(duration.Value) : null;
            result.Add(new EventCandidate
            {
                Title = title, SourceUrl = pageUrl,
                ExternalId = ticket?.AbsoluteUri,
                StartDate = date, StartTime = time,
                EndDate = end.HasValue ? DateOnly.FromDateTime(end.Value) : null,
                EndTime = end.HasValue ? TimeOnly.FromDateTime(end.Value) : null,
                City = source.City, Venue = source.Venue, Address = source.Address,
                Description = description,
                AgeLimit = age, Category = category,
                PriceMin = price.Min, PriceMax = price.Max, IsFree = price.Free,
                SourceStatus = SourceEventStatus.Unknown,
                OrgLink = ticket,
                ImageUrl = HtmlFields.Link(card.QuerySelector(".images img")?.GetAttribute("src"), pageUrl),
                Issues = issues
            });
        }
        return result.DistinctBy(item => (item.ExternalId, item.StartDate, item.StartTime))
            .GroupBy(item => item.ExternalId ?? pageUrl.AbsoluteUri)
            .SelectMany(group => group.Count() == 1 ? group : group.Select(item => item with
            {
                ExternalId = group.Key + "|" + item.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + item.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
                Issues = [.. item.Issues, new("session_identity_unconfirmed", "Для сеансов не найдены отдельные устойчивые билетные ссылки.")]
            })).ToArray();
    }

    private static int? ReadDuration(string text)
    {
        var match = Duration().Match(text);
        if (!match.Success) return null;
        var hours = int.TryParse(match.Groups[1].Value, out var h) ? h : 0;
        var minutes = int.TryParse(match.Groups[2].Value, out var m) ? m : 0;
        var total = hours * 60 + minutes;
        return total is > 0 and <= 1440 ? total : null;
    }

    private DateOnly? ReadDate(string text, string weekday)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        if (DateOnly.TryParseExact(text, "d MMMM yyyy", culture, DateTimeStyles.AllowWhiteSpaces, out var explicitDate)) return explicitDate;
        var dayOfWeek = weekday.ToUpperInvariant() switch
        {
            "ПН" => DayOfWeek.Monday, "ВТ" => DayOfWeek.Tuesday, "СР" => DayOfWeek.Wednesday,
            "ЧТ" => DayOfWeek.Thursday, "ПТ" => DayOfWeek.Friday, "СБ" => DayOfWeek.Saturday, "ВС" => DayOfWeek.Sunday,
            _ => (DayOfWeek?)null
        };
        if (!dayOfWeek.HasValue) return null;
        var year = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Processing.CandidateNormalizer.Zone).Year;
        var matches = new List<DateOnly>();
        for (var candidateYear = year - 1; candidateYear <= year + 1; candidateYear++)
            if (DateOnly.TryParseExact(text + " " + candidateYear, "d MMMM yyyy", culture, DateTimeStyles.AllowWhiteSpaces, out var date) && date.DayOfWeek == dayOfWeek)
                matches.Add(date);
        return matches.Count == 1 ? matches[0] : null;
    }

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex ExplicitYear();
    [GeneratedRegex(@"Продолжительность\s*:\s*(?:(\d{1,2})\s*час(?:а|ов)?\s*)?(?:(\d{1,3})\s*мин(?:ут(?:а|ы)?)?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();
}
