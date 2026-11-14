using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Configuration;
using Bashkiria.Events.Interaction.Contracts;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Interaction.Services;

public sealed class SubscriptionService(Transactions transactions, TimeProvider clock, IOptions<InteractionOptions> options)
{
    public Task<(Subscription Item, bool Created)> SaveAsync(string userId, Guid? id, SubscriptionRequest request, CancellationToken token)
    {
        Validate(request);
        var hash = Hash(JsonSerializer.Serialize(new { request.CityId, request.Category, request.DateFrom, request.DateTo }));
        return transactions.ExecuteAsync(async (db, ct) =>
        {
            if (!await db.Cities.AnyAsync(x => x.Id == request.CityId, ct)) throw ServiceException.Invalid("cityId", "Выберите населённый пункт из справочника.");
            var recipient = await db.Recipients.SingleOrDefaultAsync(x => x.UserId == userId && x.Channel == request.Channel, ct);
            Subscription? item = null;
            if (id.HasValue)
            {
                item = await db.Subscriptions.Include(x => x.Recipient).SingleOrDefaultAsync(x => x.Id == id && x.Recipient.UserId == userId, ct)
                    ?? throw ServiceException.Missing();
                if (item.Version != request.ExpectedVersion) throw ServiceException.Conflict();
                if (item.Recipient.Channel != request.Channel) throw ServiceException.Invalid("channel", "Для смены канала удалите подписку и создайте новую.");
                recipient = item.Recipient;
            }
            var duplicate = recipient is null ? null : await db.Subscriptions.SingleOrDefaultAsync(x => x.RecipientId == recipient.Id && x.FilterHash == hash, ct);
            if (duplicate is not null)
            {
                if (id.HasValue && duplicate.Id != id) throw ServiceException.Conflict();
                return (duplicate, false);
            }
            var now = clock.GetUtcNow();
            if (recipient is null)
            {
                recipient = new Recipient { Id = Guid.NewGuid(), UserId = userId, Channel = request.Channel, NextDigestAt = now, Version = 1 };
                await db.Recipients.AddAsync(recipient, ct);
            }
            var created = item is null;
            if (created)
            {
                var count = await db.Subscriptions.CountAsync(x => x.Recipient.UserId == userId, ct);
                if (count >= options.Value.MaxSubscriptions) throw new ServiceException(409, "subscription_limit", "Достигнут лимит подписок.");
                item = new Subscription { Id = Guid.NewGuid(), RecipientId = recipient.Id, Recipient = recipient, CreatedAt = now };
                await db.Subscriptions.AddAsync(item, ct);
            }
            else await RemovePendingAsync(db, recipient.Id, ct);
            item!.CityId = request.CityId; item.Category = request.Category; item.DateFrom = request.DateFrom; item.DateTo = request.DateTo;
            item.FilterHash = hash; item.UpdatedAt = now; item.Version++;
            recipient.Version++;
            return (item, created);
        }, token);
    }

    public Task<int> DeleteAsync(string userId, Guid? id, CancellationToken token) => transactions.ExecuteAsync(async (db, ct) =>
    {
        if (!id.HasValue)
            return await db.Recipients.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        var item = await db.Subscriptions.Include(x => x.Recipient).SingleOrDefaultAsync(x => x.Id == id && x.Recipient.UserId == userId, ct);
        if (item is null) return 0;
        await RemovePendingAsync(db, item.RecipientId, ct);
        if (!await db.Subscriptions.AnyAsync(x => x.RecipientId == item.RecipientId && x.Id != item.Id, ct)) db.Recipients.Remove(item.Recipient);
        else { db.Subscriptions.Remove(item); item.Recipient.Version++; }
        return 1;
    }, token);

    internal static async Task RemovePendingAsync(InteractionDbContext db, Guid recipientId, CancellationToken token)
    {
        var ids = db.Notifications.Where(x => x.RecipientId == recipientId && (x.Status == NotificationStatus.Pending || x.Status == NotificationStatus.Leased)).Select(x => x.Id);
        await db.NotificationEvents.Where(x => x.NotificationId != null && ids.Contains(x.NotificationId.Value)).ExecuteDeleteAsync(token);
        await db.Notifications.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(token);
    }

    private void Validate(SubscriptionRequest request)
    {
        if (!request.ConsentConfirmed) throw new ServiceException(400, "consent_required", "Подтвердите создание или изменение подписки.");
        if (request.CityId == Guid.Empty || !Enum.IsDefined(request.Channel) || request.Category.HasValue && !Enum.IsDefined(request.Category.Value))
            throw ServiceException.Invalid("filters", "Некорректный город, категория или канал.");
        var today = DigestSchedule.LocalDate(clock.GetUtcNow());
        if (request.DateTo < today || request.DateTo < request.DateFrom || request.DateFrom > today.AddDays(365) || request.DateTo > today.AddDays(365))
            throw ServiceException.Invalid("dateTo", "Проверьте период: окончание не раньше начала и сегодняшней даты, горизонт до года.");
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
