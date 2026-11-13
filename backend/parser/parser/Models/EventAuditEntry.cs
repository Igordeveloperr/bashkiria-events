namespace Bashkiria.Events.Parser.Models;

public sealed class EventAuditEntry
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
