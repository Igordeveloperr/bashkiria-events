using System.Text.RegularExpressions;
using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Configuration;
using Bashkiria.Events.Interaction.Contracts;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Interaction.Services;

public sealed class NotificationService(IDbContextFactory<InteractionDbContext> contexts, Transactions transactions,
    TimeProvider clock, IOptions<InteractionOptions> options)
{
    public IQueryable<CatalogEvent> Eligible(InteractionDbContext db, DateTimeOffset now)
    {
        var fresh = now.AddHours(-options.Value.FreshnessHours); var today = DigestSchedule.LocalDate(now);
        return db.CatalogEvents.AsNoTracking().Where(x => x.Status == "Published" && x.CityId != null && x.VenueId != null &&
            x.SourceStatus != "Cancelled" && x.SourceStatus != "Postponed" && x.VerifiedAt >= fresh && (x.EndDate ?? x.StartDate) >= today &&
            (x.DateEnd >= now || x.DateEnd == null && (x.EndDate > x.StartDate || x.DateStart >= now)));
    }

    public async Task<int> GenerateAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        if (!DigestSchedule.CanGenerate(now, options.Value.DigestHour)) return 0;
        await using var listing = await contexts.CreateDbContextAsync(token);
        var ids = await listing.Recipients.AsNoTracking().Where(x => x.NextDigestAt <= now).OrderBy(x => x.NextDigestAt).ThenBy(x => x.Id)
            .Take(100).Select(x => x.Id).ToListAsync(token);
        var added = 0;
        foreach (var id in ids)
            added += await transactions.ExecuteAsync(async (db, ct) =>
            {
                var recipient = await db.Recipients.SingleOrDefaultAsync(x => x.Id == id, ct);
                if (recipient is null || recipient.NextDigestAt > now) return 0;
                var pending = await db.Notifications.AnyAsync(x => x.RecipientId == id &&
                    (x.Status == NotificationStatus.Pending || x.Status == NotificationStatus.Leased), ct);
                if (pending) { recipient.NextDigestAt = now.AddSeconds(options.Value.PollSeconds); recipient.Version++; return 0; }
                var subscriptions = await db.Subscriptions.Where(x => x.RecipientId == id).ToListAsync(ct);
                var matches = new Dictionary<Guid, (CatalogEvent Event, List<Guid> Subscriptions)>();
                var horizon = DigestSchedule.LocalDate(now).AddDays(options.Value.HorizonDays);
                foreach (var sub in subscriptions)
                {
                    var events = await Eligible(db, now).Where(x => x.CityId == sub.CityId && (!sub.Category.HasValue || x.Category == sub.Category) &&
                        (!sub.DateFrom.HasValue || (x.EndDate ?? x.StartDate) >= sub.DateFrom) && (!sub.DateTo.HasValue || x.StartDate <= sub.DateTo) &&
                        x.StartDate <= horizon && !db.NotificationEvents.Any(sent => sent.EventId == x.Id &&
                            db.Subscriptions.Any(previous => previous.Id == sent.SubscriptionId && previous.RecipientId == id)))
                        .OrderBy(x => x.StartDate).ThenBy(x => x.StartTime).ThenBy(x => x.Id).Take(options.Value.DigestSize).ToListAsync(ct);
                    foreach (var item in events)
                    {
                        if (!matches.ContainsKey(item.Id)) matches[item.Id] = (item, []);
                        matches[item.Id].Subscriptions.Add(sub.Id);
                    }
                }
                var chosen = matches.Values.OrderBy(x => x.Event.StartDate).ThenBy(x => x.Event.StartTime).ThenBy(x => x.Event.Id).Take(options.Value.DigestSize).ToArray();
                if (chosen.Length == 0) { recipient.NextDigestAt = now.AddSeconds(options.Value.PollSeconds); recipient.Version++; return 0; }
                var notification = new Notification
                {
                    Id = Guid.NewGuid(), RecipientId = id, CreatedAt = now, AvailableAt = now, Version = 1,
                    Status = recipient.Channel == NotificationChannel.Web ? NotificationStatus.Delivered : NotificationStatus.Pending,
                    DeliveredAt = recipient.Channel == NotificationChannel.Web ? now : null
                };
                await db.Notifications.AddAsync(notification, ct);
                foreach (var match in chosen)
                    foreach (var subscriptionId in match.Subscriptions)
                        await db.NotificationEvents.AddAsync(new NotificationEvent
                        { Id = Guid.NewGuid(), NotificationId = notification.Id, SubscriptionId = subscriptionId, EventId = match.Event.Id }, ct);
                recipient.NextDigestAt = DigestSchedule.NextDay(now, options.Value.DigestHour); recipient.Version++;
                return 1;
            }, token);
        return added;
    }

    public Task<IReadOnlyList<DeliveryResponse>> ClaimAsync(int limit, CancellationToken token)
    {
        if (limit is < 1 or > 20) throw ServiceException.Invalid("limit", "Допускается от 1 до 20 уведомлений.");
        if (!DigestSchedule.CanGenerate(clock.GetUtcNow(), options.Value.DigestHour))
            return Task.FromResult<IReadOnlyList<DeliveryResponse>>([]);
        return transactions.ExecuteAsync<IReadOnlyList<DeliveryResponse>>(async (db, ct) =>
        {
            var now = clock.GetUtcNow();
            var ids = db.Recipients.Where(x => x.Channel == NotificationChannel.Telegram).Select(x => x.Id);
            var rows = await db.Notifications.Include(x => x.Events).Where(x => ids.Contains(x.RecipientId) && x.AvailableAt <= now &&
                (x.Status == NotificationStatus.Pending || x.Status == NotificationStatus.Leased && x.LeaseExpiresAt <= now))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(limit * 3).ToListAsync(ct);
            var result = new List<DeliveryResponse>();
            foreach (var row in rows)
            {
                if (result.Count == limit) break;
                if (row.Attempts >= 5) { row.Status = NotificationStatus.Failed; row.LastErrorCode = "attempts_exhausted"; row.Version++; continue; }
                var cards = await CardsAsync(db, row.Events.Select(x => x.EventId).ToArray(), ct);
                if (cards.Count == 0)
                {
                    row.Status = NotificationStatus.Suppressed; row.Version++;
                    db.NotificationEvents.RemoveRange(row.Events);
                    continue;
                }
                var quietStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DigestSchedule.LocalDate(now)
                    .ToDateTime(new TimeOnly(21, 0), DateTimeKind.Unspecified), DigestSchedule.Zone));
                row.Status = NotificationStatus.Leased; row.LeaseToken = Guid.NewGuid();
                row.LeaseExpiresAt = now.AddMinutes(2) < quietStart ? now.AddMinutes(2) : quietStart;
                row.Attempts++; row.Version++;
                var userId = await db.Recipients.Where(x => x.Id == row.RecipientId).Select(x => x.UserId).SingleAsync(ct);
                result.Add(new(row.Id, userId, row.LeaseToken.Value, row.LeaseExpiresAt.Value, cards));
            }
            return result;
        }, token);
    }

    public Task<int> AcknowledgeAsync(Guid id, DeliveryAcknowledgment request, CancellationToken token)
    {
        if (request.LeaseToken == Guid.Empty || request.ErrorCode is not null && !Regex.IsMatch(request.ErrorCode, "^[a-z0-9_]{1,64}$"))
            throw ServiceException.Invalid("leaseToken", "Некорректный токен аренды или код ошибки.");
        return transactions.ExecuteAsync(async (db, ct) =>
        {
            var item = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ServiceException.Missing();
            if (item.Status == NotificationStatus.Delivered && item.LeaseToken == request.LeaseToken && request.Success) return 0;
            var now = clock.GetUtcNow();
            if (item.Status != NotificationStatus.Leased || item.LeaseToken != request.LeaseToken || item.LeaseExpiresAt <= now) throw ServiceException.Conflict();
            item.Status = request.Success ? NotificationStatus.Delivered : request.PermanentFailure || item.Attempts >= 5 ? NotificationStatus.Failed : NotificationStatus.Pending;
            item.DeliveredAt = request.Success ? now : null; item.LeaseExpiresAt = null; item.Version++;
            item.LastErrorCode = request.Success ? null : request.ErrorCode ?? "delivery_failed";
            item.AvailableAt = request.Success ? now : now.AddSeconds(Math.Min(3600, 30 * (1 << (item.Attempts - 1))));
            if (request.Success)
            {
                var recipient = await db.Recipients.SingleAsync(x => x.Id == item.RecipientId, ct);
                var next = DigestSchedule.NextDay(now, options.Value.DigestHour);
                if (recipient.NextDigestAt < next) recipient.NextDigestAt = next;
                recipient.Version++;
            }
            return 1;
        }, token);
    }

    public async Task<IReadOnlyList<NotificationCard>> CardsAsync(InteractionDbContext db, Guid[] ids, CancellationToken token) =>
        await Eligible(db, clock.GetUtcNow()).Where(x => ids.Contains(x.Id)).OrderBy(x => x.StartDate).ThenBy(x => x.StartTime).ThenBy(x => x.Id)
            .Select(x => new NotificationCard(x.Id, x.Title, x.Category, x.StartDate, x.StartTime, x.City, x.Venue, x.Address,
                x.PriceMin, x.PriceMax, x.AgeLimit)).ToListAsync(token);

    public Task<int> CleanupAsync(CancellationToken token) => transactions.ExecuteAsync(async (db, ct) =>
    {
        var now = clock.GetUtcNow(); var today = DigestSchedule.LocalDate(now);
        var count = await db.Subscriptions.Where(x => x.DateTo < today).ExecuteDeleteAsync(ct);
        await db.Recipients.Where(x => !db.Subscriptions.Any(sub => sub.RecipientId == x.Id)).ExecuteDeleteAsync(ct);
        count += await db.Notifications.Where(x => x.CreatedAt < now.AddDays(-options.Value.NotificationRetentionDays) &&
            (x.Status != NotificationStatus.Leased || x.LeaseExpiresAt <= now)).ExecuteDeleteAsync(ct);
        count += await db.Feedback.Where(x => x.CreatedAt < now.AddDays(-options.Value.FeedbackRetentionDays)).ExecuteDeleteAsync(ct);
        return count;
    }, token);
}
