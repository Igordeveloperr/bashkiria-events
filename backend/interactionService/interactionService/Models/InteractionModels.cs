namespace Bashkiria.Events.Interaction.Models;

public enum NotificationChannel { Web, Telegram }
public enum NotificationStatus { Pending, Leased, Delivered, Suppressed, Failed }
public enum FeedbackKind { Question, Correction, OrganizerApplication }
public enum FeedbackStatus { New, InProgress, Resolved, Rejected }
public enum EventCategory { Other, Concert, Theatre, Exhibition, Festival, Cinema, Excursion, Workshop, Lecture }

public sealed class Recipient
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public DateTimeOffset NextDigestAt { get; set; }
    public long Version { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid RecipientId { get; set; }
    public Recipient Recipient { get; set; } = null!;
    public Guid CityId { get; set; }
    public EventCategory? Category { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public string FilterHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid RecipientId { get; set; }
    public NotificationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public long Version { get; set; }
    public List<NotificationEvent> Events { get; set; } = [];
}

public sealed class NotificationEvent
{
    public Guid Id { get; set; }
    public Guid? NotificationId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
}

public sealed class Feedback
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid RequestKey { get; set; }
    public string ContentHash { get; set; } = "";
    public FeedbackKind Kind { get; set; }
    public Guid? EventId { get; set; }
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public FeedbackStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class FeedbackReview
{
    public Guid Id { get; set; }
    public Guid FeedbackId { get; set; }
    public FeedbackStatus PreviousStatus { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Note { get; set; } = "";
    public string Actor { get; set; } = "admin-api";
    public DateTimeOffset CreatedAt { get; set; }
}
