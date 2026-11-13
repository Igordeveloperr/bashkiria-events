using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Configuration;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bashkiria.Events.Parser.Parsing;

public sealed class StructuredEventParser
{
    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, SourceOptions source)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var result = new List<EventCandidate>();
        foreach (var data in StructuredDataReader.ReadEvents(document))
        {
            var title = Clean(StructuredDataReader.Text(data["name"]));
            if (string.IsNullOrWhiteSpace(title)) continue;
            var url = ReadUrl(data["url"], pageUrl) ?? pageUrl;
            if (!SourceUrls.IsAllowed(url, new Uri(source.ListingUrl)) ||
                !Regex.IsMatch(url.AbsolutePath, source.EventPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
            if (StructuredDataReader.Text(data["eventAttendanceMode"])?.EndsWith("/OnlineEventAttendanceMode", StringComparison.Ordinal) == true) continue;

            var place = StructuredDataReader.Values(data["location"]).OfType<JsonObject>().FirstOrDefault();
            var address = place?["address"] as JsonObject;
            var region = StructuredDataReader.Text(address?["addressRegion"]);
            if (!string.IsNullOrWhiteSpace(region) && !Regex.IsMatch(region, "башкорт|башкир", RegexOptions.IgnoreCase)) continue;
            var city = Clean(StructuredDataReader.Text(address?["addressLocality"]) ?? source.City);
            var venue = Clean(StructuredDataReader.Text(place?["name"]) ?? source.Venue);
            var start = EventFields.Timestamp(StructuredDataReader.Text(data["startDate"]));
            var end = EventFields.Timestamp(StructuredDataReader.Text(data["endDate"]));
            var price = EventFields.Price(data);
            var issues = new List<ParsingIssue>();
            var knownCities = new[] { "Уфа", "Стерлитамак", "Салават", "Нефтекамск", "Октябрьский", "Белорецк", "Туймазы", "Ишимбай", "Сибай", "Кумертау", "Белебей", "Дюртюли", "Мелеуз", "Благовещенск", "Янаул", "Давлеканово", "Агидель", "Баймак", "Бирск", "Учалы", "Межгорье" };
            if (string.IsNullOrWhiteSpace(region) && !knownCities.Contains(city, StringComparer.OrdinalIgnoreCase) && source.City is null)
                issues.Add(new("region_unconfirmed", "Принадлежность площадки к Башкортостану требует проверки."));
            var ageText = StructuredDataReader.Text(data["contentRating"] ?? data["typicalAgeRange"]);
            int? age = EventFields.Age(ageText);
            if (age is null && int.TryParse(ageText?.TrimEnd('+'), out var ageNumber) && new[] { 0, 6, 12, 16, 18 }.Contains(ageNumber)) age = ageNumber;
            var image = StructuredDataReader.Values(data["image"]).FirstOrDefault();
            result.Add(new EventCandidate
            {
                Title = title,
                SourceUrl = SourceUrls.Canonical(url),
                ExternalId = StructuredDataReader.Text(data["@id"]),
                StartDate = start.Date,
                StartTime = start.Time,
                EndDate = end.Date,
                EndTime = end.Time,
                City = city,
                Venue = venue,
                Address = Clean(StructuredDataReader.Text(address?["streetAddress"]) ?? StructuredDataReader.Text(place?["address"]) ?? source.Address),
                Description = Clean(StructuredDataReader.Text(data["description"])),
                PriceMin = price.Min,
                PriceMax = price.Max,
                IsFree = price.Free,
                AgeLimit = age,
                Category = EventFields.Category(title + " " + StructuredDataReader.Text(data["genre"]) + " " + data["@type"]),
                SourceStatus = EventFields.Status(data["eventStatus"]),
                Language = StructuredDataReader.Text(data["inLanguage"]),
                OrgLink = ReadUrl((data["offers"] as JsonObject)?["url"], pageUrl),
                ImageUrl = ReadUrl(image is JsonObject obj ? obj["url"] : image, pageUrl),
                Latitude = EventFields.Number((place?["geo"] as JsonObject)?["latitude"]),
                Longitude = EventFields.Number((place?["geo"] as JsonObject)?["longitude"]),
                Issues = issues
            });
        }

        // Single-session page URLs stay stable across rescheduling. Multi-session pages need session identities.
        return result.DistinctBy(item => (item.SourceUrl.AbsoluteUri, item.Title, item.StartDate, item.StartTime))
            .GroupBy(item => item.SourceUrl.AbsoluteUri).SelectMany(group => group.Count() == 1 ||
                group.All(item => !string.IsNullOrEmpty(item.ExternalId)) && group.Select(item => item.ExternalId).Distinct().Count() == group.Count()
                ? group : group.Select(item => item with
                {
                    ExternalId = (item.ExternalId ?? item.SourceUrl.AbsoluteUri) + "|" + item.StartDate + "|" + item.StartTime,
                    Issues = [.. item.Issues, new("session_identity_unconfirmed", "У сеансов нет устойчивых идентификаторов; переносы требуют проверки.")]
                })).ToArray();
    }

    private static Uri? ReadUrl(JsonNode? node, Uri pageUrl) =>
        Uri.TryCreate(pageUrl, StructuredDataReader.Text(node), out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo) ? url : null;

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        using var document = new HtmlParser().ParseDocument(text);
        return Regex.Replace(document.Body?.TextContent ?? string.Empty, @"\s+", " ").Trim();
    }
}
