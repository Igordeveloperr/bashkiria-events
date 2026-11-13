namespace Bashkiria.Events.Parser.Parsing;

/// <summary>
/// Data extracted from a source before validation and publication.
/// Nullable fields represent information the source did not provide.
/// </summary>
public sealed record EventCandidate
{
    public required string Title { get; init; }
    public required Uri SourceUrl { get; init; }
    public Guid? RawDocumentId { get; init; }

    // A source may publish a date without a time. Do not invent midnight.
    public DateOnly? StartDate { get; init; }
    public TimeOnly? StartTime { get; init; }

    public string? City { get; init; }
    public string? Venue { get; init; }
    public string? Address { get; init; }
    public string? Description { get; init; }
    public decimal? PriceMin { get; init; }
    public decimal? PriceMax { get; init; }
    public int? AgeLimit { get; init; }

    public IReadOnlyList<ParsingIssue> Issues { get; init; } = [];

    public bool RequiresReview =>
        StartDate is null || StartTime is null || Issues.Count > 0;
}
