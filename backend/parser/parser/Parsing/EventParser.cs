namespace Bashkiria.Events.Parser.Parsing;

public interface IEventParser
{
    public string SourceId { get; }

    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl);
}
