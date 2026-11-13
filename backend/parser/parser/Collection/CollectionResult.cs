using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Collection;

public sealed record CollectionResult
{
    public required string SourceId { get; init; }
    public required int DiscoveredPages { get; init; }
    public required int ProcessedPages { get; init; }
    public required int DownloadedPages { get; init; }
    public required int ParsedPages { get; init; }
    public required IReadOnlyList<EventCandidate> Items { get; init; }
    public required IReadOnlyList<CollectionError> Errors { get; init; }

    public int UndatedCandidates => Items.Count(item => item.StartDate is null);
}

public sealed record CollectionError(Uri PageUrl, string Code, string Message);
