namespace Bashkiria.Events.Parser.Models;

public sealed class ParsedEvent
{
    public Guid Id { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public EventCategory Category { get; set; }
    public string[] Tags { get; set; } = [];
    public DateOnly? StartDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public DateOnly? EndDate { get; set; }
    public TimeOnly? EndTime { get; set; }
    public DateTimeOffset? DateStart { get; set; }
    public DateTimeOffset? DateEnd { get; set; }
    public int? DurationMin { get; set; }
    public string City { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }
    public bool? IsFree { get; set; }
    public int? AgeLimit { get; set; }
    public string? Language { get; set; }
    public string? OrgLink { get; set; }
    public string? ImageUrl { get; set; }
    public PublicationStatus Status { get; set; }
    public SourceEventStatus SourceStatus { get; set; }
    public decimal Confidence { get; set; }
    public string IssuesJson { get; set; } = "[]";
    public string DataHash { get; set; } = string.Empty;
    public int SourcePriority { get; set; }
    public EventSourceType SourceType { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public long Version { get; set; }
    public bool ManuallySuppressed { get; set; }
    public List<EventSourceRecord> Sources { get; set; } = [];
}
