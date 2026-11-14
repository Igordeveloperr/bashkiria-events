using Bashkiria.Events.Parser.Models;
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
    public string? ExternalId { get; init; }

    // A source may publish a date without a time. Do not invent midnight.
    public DateOnly? StartDate { get; init; }
    public TimeOnly? StartTime { get; init; }
    public DateOnly? EndDate { get; init; }
    public TimeOnly? EndTime { get; init; }

    public string? City { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CityId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? VenueId { get; init; }
    public string? Venue { get; init; }
    public string? Address { get; init; }
    public string? Description { get; init; }
    public decimal? PriceMin { get; init; }
    public decimal? PriceMax { get; init; }
    public int? AgeLimit { get; init; }
    public Models.EventCategory Category { get; init; }
    public Models.SourceEventStatus SourceStatus { get; init; }
    public bool? IsFree { get; init; }
    public string? Language { get; init; }
    public Uri? OrgLink { get; init; }
    public Uri? ImageUrl { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public string[] Tags { get; init; } = [];

    public IReadOnlyList<ParsingIssue> Issues { get; init; } = [];

    public bool RequiresReview =>
        StartDate is null || StartTime is null || Issues.Count > 0;
}
