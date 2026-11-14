using System.Text.Json;
using System.Text.RegularExpressions;
using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Configuration;
using Bashkiria.Events.Interaction.Contracts;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Interaction.Services;

public sealed class FeedbackService(Transactions transactions, TimeProvider clock, IOptions<InteractionOptions> options)
{
    public Task<(Feedback Item, bool Created)> CreateAsync(string userId, Guid key, FeedbackRequest input, CancellationToken token)
    {
        if (key == Guid.Empty) throw ServiceException.Invalid("Idempotency-Key", "Передайте непустой UUID.");
        if (!Enum.IsDefined(input.Kind)) throw ServiceException.Invalid("kind", "Неизвестный тип обращения.");
        var subject = Text(input.Subject, "subject", 200); var message = Text(input.Message, "message", 5000);
        if (!string.IsNullOrEmpty(input.Website)) throw ServiceException.Invalid("website", "Поле должно оставаться пустым.");
        if (input.Kind == FeedbackKind.Correction && input.EventId is null) throw ServiceException.Invalid("eventId", "Для исправления укажите мероприятие.");
        var hash = SubscriptionService.Hash(JsonSerializer.Serialize(new { input.Kind, input.EventId, subject, message }));
        return transactions.ExecuteAsync(async (db, ct) =>
        {
            var previous = await db.Feedback.SingleOrDefaultAsync(x => x.UserId == userId && x.RequestKey == key, ct);
            if (previous is not null)
            {
                if (previous.ContentHash != hash) throw ServiceException.Conflict();
                return (previous, false);
            }
            if (input.EventId.HasValue && !await db.CatalogEvents.AnyAsync(x => x.Id == input.EventId, ct)) throw ServiceException.Invalid("eventId", "Мероприятие не найдено.");
            var now = clock.GetUtcNow(); var since = now.AddHours(-1);
            if (await db.Feedback.CountAsync(x => x.UserId == userId && x.CreatedAt >= since, ct) >= options.Value.FeedbackPerHour)
                throw new ServiceException(429, "feedback_rate_limit", "Слишком много обращений. Повторите позже.");
            var item = new Feedback { Id = Guid.NewGuid(), UserId = userId, RequestKey = key, ContentHash = hash,
                Kind = input.Kind, EventId = input.EventId, Subject = subject, Message = message, CreatedAt = now, UpdatedAt = now, Version = 1 };
            await db.Feedback.AddAsync(item, ct);
            return (item, true);
        }, token);
    }

    public Task<int> ReviewAsync(Guid id, FeedbackReviewRequest request, CancellationToken token, string actor = "admin-api")
    {
        if (!Regex.IsMatch(actor, "^[a-zA-Z0-9_.:-]{1,128}$")) throw ServiceException.Invalid("X-Actor-Id", "Некорректный идентификатор модератора.");
        if (!Enum.IsDefined(request.Status) || request.Status == FeedbackStatus.New) throw ServiceException.Invalid("status", "Выберите статус обработки.");
        var note = Text(request.Note, "note", 2000);
        return transactions.ExecuteAsync(async (db, ct) =>
        {
            var item = await db.Feedback.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ServiceException.Missing();
            if (item.Version != request.ExpectedVersion) throw ServiceException.Conflict();
            await db.FeedbackReviews.AddAsync(new FeedbackReview { Id = Guid.NewGuid(), FeedbackId = item.Id, PreviousStatus = item.Status,
                Status = request.Status, Note = note, Actor = actor, CreatedAt = clock.GetUtcNow() }, ct);
            item.Status = request.Status; item.Version++; item.UpdatedAt = clock.GetUtcNow();
            return 1;
        }, token);
    }

    private static string Text(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Contains('\0')) throw ServiceException.Invalid(field, $"Укажите текст до {max} символов.");
        return value.Trim();
    }
}
