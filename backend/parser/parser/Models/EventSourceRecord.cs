namespace Bashkiria.Events.Parser.Models;

public sealed class EventSourceRecord
{
    public Guid Id { get; set; }
    public string IdentityKey { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public Guid EventId { get; set; }
    public ParsedEvent Event { get; set; } = null!;
    public Guid RawDocumentId { get; set; }
    public RawDocument RawDocument { get; set; } = null!;
    public string DataHash { get; set; } = string.Empty;
    public string CandidateJson { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
}
