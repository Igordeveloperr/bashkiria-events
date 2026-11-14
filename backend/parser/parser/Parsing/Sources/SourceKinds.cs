using Bashkiria.Events.Parser.Common;

namespace Bashkiria.Events.Parser.Parsing.Sources;

public static class SourceKinds
{
    public const string Gkz = "Gkz";
    public const string Structured = "Structured";
    public const string Gorodzovet = "Gorodzovet";
    public const string Bashgf = "Bashgf";
    public const string Bashopera = "Bashopera";

    public static bool Allows(string kind, Uri url) => kind switch
    {
        Gkz => SourceUrls.Host(url) == "gkzbashkortostan.ru",
        Gorodzovet => SourceUrls.Host(url) == "gorodzovet.ru",
        Bashgf => SourceUrls.Host(url) == "bashgf.ru",
        Bashopera => SourceUrls.Host(url) == "bashopera.ru",
        Structured => true,
        _ => false
    };
}

public sealed class GkzSourceParser(IEventParser parser) : ISourceEventParser
{
    public string Kind => SourceKinds.Gkz;
    public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl, Configuration.SourceOptions source) => parser.Parse(html, pageUrl);
}
