using Bashkiria.Events.Interaction.Models;

namespace Bashkiria.Events.Interaction.Contracts;

public sealed record SubscriptionRequest(Guid CityId, EventCategory? Category = null,
    DateOnly? DateFrom = null, DateOnly? DateTo = null, NotificationChannel Channel = NotificationChannel.Web,
    bool ConsentConfirmed = false, long? ExpectedVersion = null);
public sealed record FeedbackRequest(FeedbackKind Kind, string Subject, string Message, Guid? EventId = null, string? Website = null);
public sealed record FeedbackReviewRequest(long ExpectedVersion, FeedbackStatus Status, string Note);
public sealed record DeliveryAcknowledgment(Guid LeaseToken, bool Success, bool PermanentFailure = false, string? ErrorCode = null);
public sealed record InputError(string Field, string Code, string Message);
public sealed record SubscriptionResponse(Guid Id, Guid CityId, EventCategory? Category, DateOnly? DateFrom, DateOnly? DateTo,
    NotificationChannel Channel, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
public sealed record FeedbackResponse(Guid Id, FeedbackKind Kind, Guid? EventId, string Subject, string Message,
    FeedbackStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);
public sealed record PageResponse<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);
public sealed record NotificationCard(Guid Id, string Title, EventCategory Category, DateOnly? StartDate, TimeOnly? StartTime,
    string City, string Venue, string Address, decimal? PriceMin, decimal? PriceMax, int? AgeLimit);
public sealed record NotificationResponse(Guid Id, NotificationChannel Channel, NotificationStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt, IReadOnlyList<NotificationCard> Events);
public sealed record DeliveryResponse(Guid NotificationId, string UserId, Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt, IReadOnlyList<NotificationCard> Events);
