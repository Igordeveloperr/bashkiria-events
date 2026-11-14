using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Parsing;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Collection;

public sealed class SourceRegistry
{
    private readonly Dictionary<string, SourceOptions> _sources;
    private readonly Dictionary<string, ISourceEventParser> _parsers;

    public SourceRegistry(IOptions<ParserOptions> options, IEnumerable<ISourceEventParser> parsers)
    {
        _sources = options.Value.Sources.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        _parsers = parsers.ToDictionary(parser => parser.Kind, StringComparer.Ordinal);
        foreach (var source in _sources.Values)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !_parsers.ContainsKey(source.Kind) ||
                !Uri.TryCreate(source.ListingUrl, UriKind.Absolute, out var listing) || !SourceUrls.IsAllowed(listing, listing))
                throw new OptionsValidationException(ParserOptions.SectionName, typeof(ParserOptions), ["Invalid parser source configuration."]);
            _ = new Regex(source.EventPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }

    public IReadOnlyCollection<SourceOptions> Sources => _sources.Values;
    public SourceOptions Get(string id) => _sources.TryGetValue(id, out var source) && source.Enabled
        ? source : throw new KeyNotFoundException("Источник не найден или отключён.");

    public IReadOnlyList<EventCandidate> Parse(SourceOptions source, string html, Uri pageUrl) =>
        _parsers[source.Kind].Parse(html, pageUrl, source);

    public Uri? NextListingPage(SourceOptions source, string html, Uri pageUrl, int nextPage)
    {
        var next = _parsers[source.Kind].NextListingPage(html, pageUrl, nextPage);
        return next is not null && SourceUrls.IsAllowed(next, new Uri(source.ListingUrl)) ? SourceUrls.Canonical(next) : null;
    }

    public IReadOnlyList<Uri> DiscoverLinks(SourceOptions source, string html, Uri listingUrl)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var candidates = document.QuerySelectorAll(_parsers[source.Kind].ListingLinkSelector).Select(anchor => anchor.GetAttribute("href"))
            .Concat(StructuredDataReader.ReadEvents(document).Select(item => StructuredDataReader.Text(item["url"])));
        var links = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(listingUrl, candidate, out var url) ||
                !SourceUrls.IsAllowed(url, new Uri(source.ListingUrl)) ||
                !Regex.IsMatch(url.AbsolutePath, source.EventPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) continue;
            links.Add(SourceUrls.Canonical(url).AbsoluteUri);
        }
        return links.Select(link => new Uri(link)).ToArray();
    }
}
