using AngleSharp.Html.Parser;

namespace Bashkiria.Events.Parser.Parsing.Sources
{
    public sealed class GkzListingParser
    {
        public IReadOnlyList<Uri> ParseLinks(string html, Uri listingUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(html);
            ArgumentNullException.ThrowIfNull(listingUrl);

            if (!listingUrl.IsAbsoluteUri)
            {
                throw new ArgumentException(
                    "Адрес афиши должен быть абсолютным.",
                    nameof(listingUrl));
            }

            var htmlParser = new HtmlParser();
            using var document = htmlParser.ParseDocument(html);

            var links = new HashSet<Uri>();

            foreach (var anchor in document.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");

                if (string.IsNullOrWhiteSpace(href) ||
                    !Uri.TryCreate(listingUrl, href, out var url))
                {
                    continue;
                }

                if (url.Scheme != Uri.UriSchemeHttps ||
                    url.Host != "gkzbashkortostan.ru" ||
                    !url.IsDefaultPort ||
                    !string.IsNullOrEmpty(url.UserInfo))
                {
                    continue;
                }

                var segments = url.AbsolutePath.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length != 2 || segments[0] != "events")
                {
                    continue;
                }

                var canonicalUrl = new UriBuilder(url)
                {
                    Query = string.Empty,
                    Fragment = string.Empty,
                    Path = url.AbsolutePath.TrimEnd('/') + "/"
                }.Uri;

                links.Add(canonicalUrl);
            }

            return links.ToArray();
        }
    }
}
