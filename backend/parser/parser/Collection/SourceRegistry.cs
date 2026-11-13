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
    private readonly IEventParser _gkz;
    private readonly StructuredEventParser _structured;

    public SourceRegistry(IOptions<ParserOptions> options, IEventParser gkz, StructuredEventParser structured)
    {
        _sources = options.Value.Sources.ToDictionary(source => source.Id, StringComparer.OrdinalIgnoreCase);
        _gkz = gkz;
        _structured = structured;
        foreach (var source in _sources.Values)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || source.Kind is not ("Gkz" or "Structured") ||
                !Uri.TryCreate(source.ListingUrl, UriKind.Absolute, out var listing) || !SourceUrls.IsAllowed(listing, listing))
                throw new OptionsValidationException(ParserOptions.SectionName, typeof(ParserOptions), ["Invalid parser source configuration."]);
            _ = new Regex(source.EventPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }

    public IReadOnlyCollection<SourceOptions> Sources => _sources.Values;
    public SourceOptions Get(string id) => _sources.TryGetValue(id, out var source) && source.Enabled
        ? source : throw new KeyNotFoundException("Источник не найден или отключён.");

    public IReadOnlyList<EventCandidate> Parse(SourceOptions source, string html, Uri pageUrl) =>
        source.Kind == "Gkz" ? _gkz.Parse(html, pageUrl) : _structured.Parse(html, pageUrl, source);

    public IReadOnlyList<Uri> DiscoverLinks(SourceOptions source, string html, Uri listingUrl)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var candidates = document.QuerySelectorAll("a[href]").Select(anchor => anchor.GetAttribute("href"))
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
