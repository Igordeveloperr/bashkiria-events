using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Common;

namespace Bashkiria.Events.Parser.Parsing.Sources
{
    public sealed class GkzParser : IEventParser
    {
        private const string GkzCity = "Уфа";
        private const string GkzVenue = "ГКЗ «Башкортостан»";
        private const string GkzAddress = "ул. Ленина, 50";
        private const string MecSingleEventDescription = ".mec-single-event-description";

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

            return
            [
                new EventCandidate
            {
                Title = title,
                SourceUrl = pageUrl,
                Description = description,
                City = GkzCity,
                Venue = GkzVenue,
                Address = GkzAddress
            }
            ];
        }
    }
}
