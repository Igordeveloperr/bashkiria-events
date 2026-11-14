using System.Text.Json.Nodes;
using Bashkiria.Events.Parser.Common;
using System.Text.Json;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Microsoft.EntityFrameworkCore;
using Bashkiria.Events.Parser.References;

namespace Bashkiria.Events.Parser.Processing;

public sealed class EventReviewService(IDbContextFactory<ParserDbContext> contexts, CandidateNormalizer normalizer, TimeProvider clock,
    VenueResolver resolver)
{
    public async Task ReviewAsync(Guid id, ReviewEventRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Status) || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ParserValidationException("Укажите статус и обоснование проверки.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Events.Include(item => item.Sources).SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (entity.Version != request.ExpectedVersion) throw new DbUpdateConcurrencyException();
        if (entity.SourceStatus == SourceEventStatus.Cancelled && request.Status == PublicationStatus.Published)
            throw new ParserValidationException("Источник сообщает об отмене. Перед публикацией требуется обновление данных источника.");
        var before = ParserJson.Serialize(new
        {
            entity.Title,
            entity.City,
            entity.Venue,
            entity.Address,
            entity.Description,
            entity.Status,
            entity.StartDate,
            entity.StartTime,
            entity.PriceMin,
            entity.PriceMax,
            entity.AgeLimit,
            entity.SourceStatus,
            entity.ReviewNote
        });
        var candidate = new EventCandidate
        {
            Title = request.Title ?? entity.Title,
            SourceUrl = new Uri(entity.Sources.First().SourceUrl),
            StartDate = request.StartDate ?? entity.StartDate,
            StartTime = request.StartTime ?? entity.StartTime,
            EndDate = entity.EndDate,
            EndTime = entity.EndTime,
            City = request.City ?? entity.City,
            CityId = request.CityId ?? (request.City is null ? entity.CityId : null),
            VenueId = request.VenueId ?? (request.City is null && request.CityId is null && request.Venue is null && request.Address is null ? entity.VenueId : null),
            Venue = request.Venue ?? entity.Venue,
            Address = request.Address ?? entity.Address,
            Description = request.Description ?? entity.Description,
            Category = entity.Category,
            Tags = entity.Tags,
            PriceMin = request.PriceMin ?? entity.PriceMin,
            PriceMax = request.PriceMax ?? entity.PriceMax,
            AgeLimit = request.AgeLimit ?? entity.AgeLimit,
            SourceStatus = request.ConfirmedSourceStatus ?? entity.SourceStatus,
            IsFree = (request.PriceMin ?? entity.PriceMin) == 0 && (request.PriceMax ?? entity.PriceMax) == 0
                ? true : (request.PriceMin ?? entity.PriceMin) > 0 ? false : entity.IsFree
        };
        candidate = await resolver.ResolveAsync(db, candidate, cancellationToken);
        var normalized = normalizer.Normalize("review", candidate);
        if (request.ConfirmedSourceStatus.HasValue && !Enum.IsDefined(request.ConfirmedSourceStatus.Value) ||
            normalized.Candidate.Issues.Any(issue => issue.Code is "invalid_price" or "invalid_age" or "invalid_end" or "price_conflict"))
            throw new ParserValidationException("Некорректные значения полей мероприятия.");
        if (request.Status == PublicationStatus.Published && (normalized.Candidate.RequiresReview ||
            normalized.Candidate.Issues.Count > 0 || candidate.SourceStatus is SourceEventStatus.Cancelled or SourceEventStatus.Postponed))
            throw new ParserValidationException("Для публикации необходимо заполнить обязательные поля и подтвердить действующий статус.");

        entity.StartDate = candidate.StartDate; entity.StartTime = candidate.StartTime;
        entity.Title = normalized.Candidate.Title; entity.NormalizedTitle = normalized.NormalizedTitle;
        entity.City = normalized.Candidate.City ?? string.Empty; entity.Venue = normalized.Candidate.Venue ?? string.Empty;
        entity.CityId = candidate.CityId; entity.VenueId = candidate.VenueId;
        entity.Address = normalized.Candidate.Address ?? string.Empty; entity.Description = normalized.Candidate.Description ?? string.Empty;
        entity.DateStart = normalized.StartUtc; entity.PriceMin = candidate.PriceMin; entity.PriceMax = candidate.PriceMax;
        entity.DateEnd = normalized.EndUtc;
        entity.DurationMin = normalized.StartUtc.HasValue && normalized.EndUtc.HasValue
            ? (int)(normalized.EndUtc.Value - normalized.StartUtc.Value).TotalMinutes : null;
        entity.IsFree = candidate.IsFree; entity.AgeLimit = candidate.AgeLimit; entity.SourceStatus = candidate.SourceStatus;
        entity.DedupKey = normalized.DedupKey; entity.Status = request.Status;
        entity.IssuesJson = ParserJson.Serialize(normalized.Candidate.Issues); entity.Confidence = normalized.Confidence;
        entity.ReviewedAt = clock.GetUtcNow(); entity.ReviewNote = request.Note.Trim(); entity.UpdatedAt = clock.GetUtcNow();
        if (entity.SourceType != EventSourceType.Page && request.Status == PublicationStatus.Published)
            entity.VerifiedAt = entity.ReviewedAt.Value;
        entity.ManuallySuppressed = request.Status != PublicationStatus.Published;
        entity.Version++;
        await db.AuditEntries.AddAsync(new EventAuditEntry
        {
            Id = Guid.NewGuid(),
            EventId = id,
            Action = "moderated",
            Actor = "api-key",
            CreatedAt = clock.GetUtcNow(),
            SnapshotJson = ParserJson.Serialize(new { Before = JsonNode.Parse(before), Review = request })
        }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
