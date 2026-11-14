using Bashkiria.Events.Parser.Configuration;

namespace Bashkiria.Events.Parser.Parsing;

public interface ISourceEventParser
{
    string Kind { get; }
    string ListingLinkSelector => "a[href]";
    IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, SourceOptions source);
    Uri? NextListingPage(string html, Uri pageUrl, int nextPage) => null;
}
