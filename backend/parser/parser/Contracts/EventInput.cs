using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Contracts;

public sealed record EventInput
{
    public string? ExternalId { get; init; }
    public string? Title { get; init; }
    public Uri? SourceUrl { get; init; }
    public EventCategory Category { get; init; }
    public string[]? Tags { get; init; }
    public DateOnly? StartDate { get; init; }
    public TimeOnly? StartTime { get; init; }
    public DateOnly? EndDate { get; init; }
    public TimeOnly? EndTime { get; init; }
    public string? City { get; init; }
    public string? Venue { get; init; }
    public string? Address { get; init; }
    public string? Description { get; init; }
    public decimal? Latitude { get; init; }
    public decimal? Longitude { get; init; }
    public decimal? PriceMin { get; init; }
    public decimal? PriceMax { get; init; }
    public bool? IsFree { get; init; }
    public int? AgeLimit { get; init; }
    public string? Language { get; init; }
    public Uri? OrgLink { get; init; }
    public Uri? ImageUrl { get; init; }
    public SourceEventStatus SourceStatus { get; init; }
}

public sealed record InputError(string Field, string Code, string Message);

public sealed record ImportRowResponse(int Row, string? ExternalId, Guid? EventId, string Outcome,
    PublicationStatus? Status, IReadOnlyList<ParsingIssue> Issues, IReadOnlyList<InputError> Errors);

public sealed record ImportResponse(Guid RawDocumentId, string? DatasetId, IReadOnlyList<ImportRowResponse> Items)
{
    public int Total => Items.Count;
    public int Added => Items.Count(item => item.Outcome == "added");
    public int Updated => Items.Count(item => item.Outcome == "updated");
    public int Unchanged => Items.Count(item => item.Outcome == "unchanged");
    public int Rejected => Items.Count(item => item.Outcome == "rejected");
    public int RequiresReview => Items.Count(item => item.Status == PublicationStatus.Draft);
}

public sealed record CreateEventResponse(Guid EventId, string Outcome, PublicationStatus Status,
    Guid RawDocumentId, IReadOnlyList<ParsingIssue> Issues)
{
    public bool RequiresReview => Status == PublicationStatus.Draft;
}
