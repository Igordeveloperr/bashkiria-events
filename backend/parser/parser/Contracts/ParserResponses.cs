using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Contracts;

public sealed record PageResponse<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);
public sealed record EventSourceResponse(string SourceId, string SourceUrl, Guid RawDocumentId, DateTimeOffset CheckedAt);
public sealed record CollectionRunResponse(Guid Id, string SourceId, CollectionRunStatus Status, int MaxPages, int Attempts,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error, CollectionResult? Report);

public sealed class EventResponse
{
    public Guid Id { get; init; }
    public required string Title { get; init; }
    public required string NormalizedTitle { get; init; }
    public EventCategory Category { get; init; }
    public required string[] Tags { get; init; }
    public DateOnly? StartDate { get; init; }
    public TimeOnly? StartTime { get; init; }
    public DateOnly? EndDate { get; init; }
    public TimeOnly? EndTime { get; init; }
    public DateTimeOffset? DateStart { get; init; }
    public DateTimeOffset? DateEnd { get; init; }
    public int? DurationMin { get; init; }
    public string TimeZone { get; init; } = "Asia/Yekaterinburg";
    public required string City { get; init; }
    public required string Venue { get; init; }
    public required string Address { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public required string Description { get; init; }
    public decimal? PriceMin { get; init; }
    public decimal? PriceMax { get; init; }
    public bool? IsFree { get; init; }
    public int? AgeLimit { get; init; }
    public string? Language { get; init; }
    public string? OrgLink { get; init; }
    public string? ImageUrl { get; init; }
    public PublicationStatus Status { get; init; }
    public SourceEventStatus SourceStatus { get; init; }
    public decimal Confidence { get; init; }
    public required IReadOnlyList<ParsingIssue> Issues { get; init; }
    public bool RequiresReview { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? ReviewNote { get; init; }
    public long Version { get; init; }
    public required IReadOnlyList<EventSourceResponse> Sources { get; init; }
    public string SourceType { get; init; } = "page";
    public required string SourceHash { get; init; }
    public required string DedupKey { get; init; }
}
