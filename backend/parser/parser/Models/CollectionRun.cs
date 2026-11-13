namespace Bashkiria.Events.Parser.Models;

public sealed class CollectionRun
{
    public Guid Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public CollectionRunStatus Status { get; set; }
    public int MaxPages { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? Error { get; set; }
    public string? ReportJson { get; set; }
    public string? ScheduleKey { get; set; }
}
