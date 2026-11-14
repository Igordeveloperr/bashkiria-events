using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Contracts;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Models;
using Bashkiria.Events.Interaction.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Interaction.Api;

public static class InteractionEndpoints
{
    public static void MapInteractionEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/api/v1/interaction/me").WithMetadata(new ServiceScope(AccessScope.User)).RequireRateLimiting("interaction").WithTags("User");
        var admin = app.MapGroup("/api/v1/interaction/admin").WithMetadata(new ServiceScope(AccessScope.Admin)).RequireRateLimiting("interaction").WithTags("Admin");
        var delivery = app.MapGroup("/api/v1/interaction/deliveries").WithMetadata(new ServiceScope(AccessScope.Delivery)).RequireRateLimiting("interaction").WithTags("Delivery");

        me.MapPost("/subscriptions", async (SubscriptionRequest request, HttpContext http, SubscriptionService service, CancellationToken ct) =>
        {
            var result = await service.SaveAsync(ServiceAccess.User(http), null, request, ct);
            return result.Created ? Results.Created($"/api/v1/interaction/me/subscriptions/{result.Item.Id}", SubscriptionDto(result.Item)) : Results.Ok(SubscriptionDto(result.Item));
        }).Produces<SubscriptionResponse>(201).Produces<SubscriptionResponse>(200).WithName("subscribe");
        me.MapPut("/subscriptions/{id:guid}", async (Guid id, SubscriptionRequest request, HttpContext http, SubscriptionService service, CancellationToken ct) =>
            Results.Ok(SubscriptionDto((await service.SaveAsync(ServiceAccess.User(http), id, request, ct)).Item))).Produces<SubscriptionResponse>();
        me.MapDelete("/subscriptions/{id:guid}", async (Guid id, HttpContext http, SubscriptionService service, CancellationToken ct) =>
        { await service.DeleteAsync(ServiceAccess.User(http), id, ct); return Results.NoContent(); });
        me.MapDelete("/subscriptions", async (HttpContext http, SubscriptionService service, CancellationToken ct) =>
        { await service.DeleteAsync(ServiceAccess.User(http), null, ct); return Results.NoContent(); });
        me.MapGet("/subscriptions", async (int? page, int? pageSize, HttpContext http, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var (number, size) = Pagination(page, pageSize); var user = ServiceAccess.User(http);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var query = db.Subscriptions.AsNoTracking().Include(x => x.Recipient).Where(x => x.Recipient.UserId == user);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new PageResponse<SubscriptionResponse>(total, number, size, rows.Select(SubscriptionDto).ToArray()));
        }).Produces<PageResponse<SubscriptionResponse>>();
        me.MapGet("/subscriptions/{id:guid}", async (Guid id, HttpContext http, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var user = ServiceAccess.User(http); await using var db = await contexts.CreateDbContextAsync(ct);
            var row = await db.Subscriptions.AsNoTracking().Include(x => x.Recipient).SingleOrDefaultAsync(x => x.Id == id && x.Recipient.UserId == user, ct);
            return row is null ? ApiErrors.Result(404) : Results.Ok(SubscriptionDto(row));
        }).Produces<SubscriptionResponse>();
        me.MapGet("/notifications", async (int? page, int? pageSize, bool? unreadOnly, HttpContext http, NotificationService service,
            IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var (number, size) = Pagination(page, pageSize); var user = ServiceAccess.User(http);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var recipients = db.Recipients.Where(x => x.UserId == user).Select(x => x.Id);
            var query = db.Notifications.AsNoTracking().Where(x => recipients.Contains(x.RecipientId) && x.Status != NotificationStatus.Suppressed &&
                (unreadOnly != true || x.ReadAt == null));
            var count = await query.CountAsync(ct);
            var rows = await query.Include(x => x.Events).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            var channels = await db.Recipients.Where(x => x.UserId == user).ToDictionaryAsync(x => x.Id, x => x.Channel, ct);
            var cards = await service.CardsAsync(db, rows.SelectMany(x => x.Events).Select(x => x.EventId).Distinct().ToArray(), ct);
            var result = new List<NotificationResponse>();
            foreach (var row in rows)
            {
                var eventIds = row.Events.Select(x => x.EventId).ToHashSet();
                result.Add(new(row.Id, channels[row.RecipientId], row.Status, row.CreatedAt, row.DeliveredAt, row.ReadAt,
                    cards.Where(card => eventIds.Contains(card.Id)).ToArray()));
            }
            return Results.Ok(new PageResponse<NotificationResponse>(count, number, size, result));
        }).Produces<PageResponse<NotificationResponse>>();
        me.MapPatch("/notifications/{id:guid}/read", async (Guid id, HttpContext http, IDbContextFactory<InteractionDbContext> contexts, TimeProvider clock, CancellationToken ct) =>
        {
            var user = ServiceAccess.User(http); await using var db = await contexts.CreateDbContextAsync(ct);
            var query = db.Notifications.Where(x => x.Id == id && db.Recipients.Any(r => r.Id == x.RecipientId && r.UserId == user));
            if (!await query.AnyAsync(ct)) throw ServiceException.Missing();
            await query.Where(x => x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, clock.GetUtcNow()).SetProperty(x => x.Version, x => x.Version + 1), ct);
            return Results.NoContent();
        });
        me.MapPost("/feedback", async (FeedbackRequest request, [FromHeader(Name = "Idempotency-Key")] Guid key,
            HttpContext http, FeedbackService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(ServiceAccess.User(http), key, request, ct);
            return result.Created ? Results.Created($"/api/v1/interaction/me/feedback/{result.Item.Id}", FeedbackDto(result.Item)) : Results.Ok(FeedbackDto(result.Item));
        }).Produces<FeedbackResponse>(201).Produces<FeedbackResponse>(200).WithName("save_feedback");
        me.MapGet("/feedback", async (int? page, int? pageSize, HttpContext http, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var (number, size) = Pagination(page, pageSize); var user = ServiceAccess.User(http);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var query = db.Feedback.AsNoTracking().Where(x => x.UserId == user);
            var count = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new PageResponse<FeedbackResponse>(count, number, size, rows.Select(FeedbackDto).ToArray()));
        }).Produces<PageResponse<FeedbackResponse>>();
        me.MapGet("/feedback/{id:guid}", async (Guid id, HttpContext http, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var user = ServiceAccess.User(http); await using var db = await contexts.CreateDbContextAsync(ct);
            var row = await db.Feedback.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == user, ct);
            return row is null ? ApiErrors.Result(404) : Results.Ok(FeedbackDto(row));
        }).Produces<FeedbackResponse>();
        admin.MapGet("/feedback", async (FeedbackStatus? status, FeedbackKind? kind, int? page, int? pageSize, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            var (number, size) = Pagination(page, pageSize);
            if (status.HasValue && !Enum.IsDefined(status.Value) || kind.HasValue && !Enum.IsDefined(kind.Value)) throw ServiceException.Invalid("status", "Неизвестный статус или тип.");
            await using var db = await contexts.CreateDbContextAsync(ct);
            var query = db.Feedback.AsNoTracking().Where(x => (!status.HasValue || x.Status == status) && (!kind.HasValue || x.Kind == kind));
            var count = await query.CountAsync(ct);
            var rows = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
            return Results.Ok(new PageResponse<FeedbackResponse>(count, number, size, rows.Select(FeedbackDto).ToArray()));
        }).Produces<PageResponse<FeedbackResponse>>();
        admin.MapPatch("/feedback/{id:guid}/review", async (Guid id, FeedbackReviewRequest request, HttpContext http, FeedbackService service, CancellationToken ct) =>
        { await service.ReviewAsync(id, request, ct, http.Request.Headers["X-Actor-Id"].FirstOrDefault() ?? "admin-api"); return Results.NoContent(); });
        admin.MapGet("/feedback/{id:guid}/history", async (Guid id, IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            if (!await db.Feedback.AnyAsync(x => x.Id == id, ct)) throw ServiceException.Missing();
            return Results.Ok(await db.FeedbackReviews.AsNoTracking().Where(x => x.FeedbackId == id).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(100)
                .Select(x => new { x.Id, x.PreviousStatus, x.Status, x.Note, x.Actor, x.CreatedAt }).ToListAsync(ct));
        });
        admin.MapPost("/notifications/generate", async (NotificationService service, CancellationToken ct) => Results.Ok(new { created = await service.GenerateAsync(ct) }));
        admin.MapPost("/retention/cleanup", async (NotificationService service, CancellationToken ct) => Results.Ok(new { deleted = await service.CleanupAsync(ct) }));
        admin.MapGet("/metrics", (ApiMetrics metrics) => Results.Ok(metrics.Snapshot()));
        delivery.MapPost("/claim", async (int? limit, NotificationService service, CancellationToken ct) => Results.Ok(await service.ClaimAsync(limit ?? 10, ct))).Produces<IReadOnlyList<DeliveryResponse>>();
        delivery.MapPost("/{id:guid}/ack", async (Guid id, DeliveryAcknowledgment request, NotificationService service, CancellationToken ct) =>
        { await service.AcknowledgeAsync(id, request, ct); return Results.NoContent(); });
    }

    private static SubscriptionResponse SubscriptionDto(Subscription x) => new(x.Id, x.CityId, x.Category, x.DateFrom, x.DateTo,
        x.Recipient.Channel, x.CreatedAt, x.UpdatedAt, x.Version);
    private static FeedbackResponse FeedbackDto(Feedback x) => new(x.Id, x.Kind, x.EventId, x.Subject, x.Message, x.Status, x.CreatedAt, x.UpdatedAt, x.Version);
    private static (int Number, int Size) Pagination(int? page, int? pageSize)
    {
        var number = page ?? 1; var size = pageSize ?? 30;
        if (number is < 1 or > 100000 || size is < 1 or > 100) throw ServiceException.Invalid("page", "page: 1..100000, pageSize: 1..100.");
        return (number, size);
    }
}
