using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Common;
using System.Globalization;
using AngleSharp.Dom;
using Microsoft.AspNetCore.WebUtilities;

namespace Bashkiria.Events.Parser.Parsing.Sources
{
    public sealed class GkzParser : IEventParser
    {
        private const string GkzCity = "Уфа";
        private const string GkzVenue = "ГКЗ «Башкортостан»";
        private const string GkzAddress = "ул. Ленина, 50";
        private const string MecSingleEventDescription = ".mec-single-event-description";
        private const string GkzTimeZone = "Asia/Yekaterinburg";

        public string SourceId => SourceIdentificator.Gkz;

        public IReadOnlyList<EventCandidate> Parse(
            string html,
            Uri pageUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(html);
            ArgumentNullException.ThrowIfNull(pageUrl);

            var htmlParser = new HtmlParser();
            using var document = htmlParser.ParseDocument(html);

            var title = document.QuerySelector(ParserQuerySelector.H1)
                ?.TextContent.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return [];
            }

            var description = document
                .QuerySelector(MecSingleEventDescription)
                ?.TextContent.Trim();

            var calendarStart = ReadStartDateTime(document, pageUrl);
            var schedule = GkzScheduleReader.Read(document, calendarStart);

            return
            [
                new EventCandidate
            {
                Title = title,
                SourceUrl = pageUrl,
                Description = description,
                City = GkzCity,
                Venue = GkzVenue,
                Address = GkzAddress,
                StartDate = schedule.Date,
                StartTime = schedule.Time,
                Issues = schedule.Issues,
            }
            ];
        }

        private static DateTime? ReadStartDateTime(
            IDocument document,
            Uri pageUrl)
        {
            foreach (var anchor in document.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");

                if (string.IsNullOrWhiteSpace(href) ||
                    !Uri.TryCreate(pageUrl, href, out var calendarUrl))
                {
                    continue;
                }

                if (calendarUrl.Scheme != Uri.UriSchemeHttps ||
                    (calendarUrl.Host != "calendar.google.com" &&
                     calendarUrl.Host != "www.google.com"))
                {
                    continue;
                }

                if (calendarUrl.AbsolutePath != "/calendar" &&
                    !calendarUrl.AbsolutePath.StartsWith(
                        "/calendar/",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var query = QueryHelpers.ParseQuery(calendarUrl.Query);

                if (!query.TryGetValue("dates", out var dates))
                {
                    continue;
                }

                query.TryGetValue("ctz", out var timeZoneValues);
                var timeZone = timeZoneValues.ToString();

                if (!string.IsNullOrEmpty(timeZone) &&
                    timeZone != GkzTimeZone)
                {
                    continue;
                }

                var rawStart = dates.ToString().Split('/')[0];
                var isUtc = rawStart.EndsWith('Z');

                // Время без Z принимаем только с явно указанным часовым поясом.
                if (!isUtc && timeZone != GkzTimeZone)
                {
                    continue;
                }

                var timestamp = isUtc ? rawStart[..^1] : rawStart;

                if (!DateTime.TryParseExact(
                        timestamp,
                        "yyyyMMdd'T'HHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var start))
                {
                    continue;
                }

                if (isUtc)
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(
                        GkzTimeZone);

                    start = TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(start, DateTimeKind.Utc),
                        zone);
                }

                return start;
            }

            return null;
        }
    }
}
