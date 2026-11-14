using System.Text.Json.Nodes;
using Bashkiria.Events.Parser.Common;
using System.Data;
using System.Text.Json;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Parser.Storage;

public sealed class EfEventStore(IDbContextFactory<ParserDbContext> contexts, CandidateNormalizer normalizer) : IEventStore
{
    private const int MaxAttempts = 6;
    public async Task<EventSaveResult> SaveAsync(EventWriteContext source, EventCandidate candidate, RawDocument document, CancellationToken cancellationToken)
    {
        if (document.SourceId != source.Id || document.SourceType != source.SourceType ||
            source.SourceType == EventSourceType.Page && document.HttpStatus is not (>= 200 and <= 299))
            throw new ArgumentException("Кандидат должен опираться на документ своего источника; для страницы требуется успешный HTTP-ответ.");
        var normalized = normalizer.Normalize(source.Id, candidate with { RawDocumentId = document.Id });
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SaveOnceAsync(source, normalized, document, cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts - 1 && DatabaseErrors.IsConflict(exception))
            {
                // Retry the entire transaction, not an individual INSERT. Jitter prevents
                // concurrent writers from repeatedly meeting on the same predicate locks.
                var delay = 50 * (1 << attempt) + Random.Shared.Next(25, 76);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
            catch (Exception exception) when (DatabaseErrors.IsDatabaseFailure(exception))
            {
                throw new ParserStorageException("Could not save the normalized event.", exception);
            }
        }
    }

    private async Task<EventSaveResult> SaveOnceAsync(EventWriteContext source, NormalizedCandidate normalized,
        RawDocument document, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var provenance = await db.EventSources.Include(item => item.Event)
            .SingleOrDefaultAsync(item => item.IdentityKey == normalized.IdentityKey, cancellationToken);
        if (provenance is not null && provenance.CheckedAt > document.FetchedAt)
            return new(provenance.EventId, "unchanged", provenance.Event.Status);

        if (provenance is not null && provenance.DataHash == normalized.DataHash)
        {
            // Preserve manual corrections and merged identities when the source itself has not changed.
            provenance.RawDocumentId = document.Id;
            provenance.CheckedAt = document.FetchedAt;
            if (provenance.Event.DataHash == normalized.DataHash)
            {
                if (document.FetchedAt > provenance.Event.VerifiedAt) provenance.Event.VerifiedAt = document.FetchedAt;
                if (document.FetchedAt > provenance.Event.UpdatedAt) provenance.Event.UpdatedAt = document.FetchedAt;
                provenance.Event.Version++;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(provenance.EventId, "unchanged", provenance.Event.Status);
        }

        var entity = provenance?.Event;
        var before = provenance?.CandidateJson;
        if (entity is null || entity.DedupKey != normalized.DedupKey)
        {
            var matching = await db.Events.SingleOrDefaultAsync(item => item.DedupKey == normalized.DedupKey, cancellationToken);
            if (matching is not null) entity = matching;
            else if (entity is not null && await db.EventSources.CountAsync(item => item.EventId == entity.Id, cancellationToken) > 1)
                entity = null;
        }

        var added = entity is null;
        entity ??= new ParsedEvent { Id = Guid.NewGuid(), CreatedAt = document.FetchedAt };
        var changed = entity.DataHash != normalized.DataHash;
        var canReplace = added || source.Priority >= entity.SourcePriority &&
            (source.AllowMergeReplacement || provenance?.EventId == entity.Id);
        if (changed && canReplace)
        {
            Apply(entity, normalized, source, document.FetchedAt);
            await db.AuditEntries.AddAsync(new EventAuditEntry
            {
                Id = Guid.NewGuid(),
                EventId = entity.Id,
                Action = added ? source.SourceType switch { EventSourceType.Manual => "created_manually", EventSourceType.Import => "imported", _ => "collected" } : "source_updated",
                Actor = source.Actor ?? "source:" + source.Id,
                CreatedAt = document.FetchedAt,
                SnapshotJson = ParserJson.Serialize(new { Before = before is null ? null : JsonNode.Parse(before), After = JsonNode.Parse(normalized.CandidateJson), RawDocumentId = document.Id })
            }, cancellationToken);
        }
        else if (!changed)
        {
            if (document.FetchedAt > entity.VerifiedAt) entity.VerifiedAt = document.FetchedAt;
            if (document.FetchedAt > entity.UpdatedAt) entity.UpdatedAt = document.FetchedAt;
            entity.Version++;
        }
        else if (source.SourceType == EventSourceType.Page && normalized.Candidate.SourceStatus is (SourceEventStatus.Cancelled or SourceEventStatus.Postponed))
        {
            // A conflicting cancellation must never leave the old card in public results.
            entity.Status = normalized.Candidate.SourceStatus == SourceEventStatus.Cancelled ? PublicationStatus.Cancelled : PublicationStatus.Draft;
            entity.SourceStatus = normalized.Candidate.SourceStatus;
            entity.IssuesJson = ParserJson.Serialize(new[] { new ParsingIssue("source_status_conflict", "Источники расходятся в статусе мероприятия.") });
            entity.ReviewedAt = null;
            entity.UpdatedAt = document.FetchedAt;
            entity.Version++;
        }
        if (changed && !canReplace)
            await db.AuditEntries.AddAsync(new EventAuditEntry
            {
                Id = Guid.NewGuid(),
                EventId = entity.Id,
                Action = "source_observed",
                Actor = source.Actor ?? "source:" + source.Id,
                CreatedAt = document.FetchedAt,
                SnapshotJson = ParserJson.Serialize(new { Before = before is null ? null : JsonNode.Parse(before), After = JsonNode.Parse(normalized.CandidateJson), RawDocumentId = document.Id })
            }, cancellationToken);
        if (added) await db.Events.AddAsync(entity, cancellationToken);

        if (provenance is null)
        {
            provenance = new EventSourceRecord { Id = Guid.NewGuid(), IdentityKey = normalized.IdentityKey };
            await db.EventSources.AddAsync(provenance, cancellationToken);
        }
        var previousEventId = provenance.EventId;
        provenance.Event = entity;
        provenance.EventId = entity.Id;
        provenance.SourceId = source.Id;
        provenance.SourceType = source.SourceType;
        provenance.ExternalId = normalized.Candidate.ExternalId ?? normalized.Candidate.SourceUrl.AbsoluteUri;
        provenance.SourceUrl = normalized.Candidate.SourceUrl.AbsoluteUri;
        provenance.RawDocumentId = document.Id;
        provenance.CheckedAt = document.FetchedAt;
        provenance.CandidateJson = normalized.CandidateJson;
        provenance.DataHash = normalized.DataHash;

        // If a source was merged into an existing card, retain the old URL but withdraw an orphaned public card.
        if (previousEventId != Guid.Empty && previousEventId != entity.Id &&
            !await db.EventSources.AnyAsync(item => item.EventId == previousEventId && item.Id != provenance.Id, cancellationToken))
        {
            var previous = await db.Events.FindAsync([previousEventId], cancellationToken);
            if (previous is not null) { previous.Status = PublicationStatus.Draft; previous.Version++; previous.UpdatedAt = document.FetchedAt; }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(entity.Id, added ? "added" : changed && canReplace ? "updated" : "unchanged", entity.Status);
    }

    private static void Apply(ParsedEvent entity, NormalizedCandidate normalized, EventWriteContext source, DateTimeOffset checkedAt)
    {
        var suppressedStatus = entity.Status;
        var item = normalized.Candidate;
        entity.DedupKey = normalized.DedupKey; entity.Title = item.Title; entity.NormalizedTitle = normalized.NormalizedTitle;
        entity.Category = item.Category; entity.Tags = item.Tags; entity.StartDate = item.StartDate; entity.StartTime = item.StartTime;
        entity.EndDate = item.EndDate; entity.EndTime = item.EndTime; entity.DateStart = normalized.StartUtc; entity.DateEnd = normalized.EndUtc;
        entity.DurationMin = normalized.StartUtc.HasValue && normalized.EndUtc.HasValue
            ? (int)(normalized.EndUtc.Value - normalized.StartUtc.Value).TotalMinutes : null;
        entity.City = item.City ?? string.Empty; entity.Venue = item.Venue ?? string.Empty; entity.Address = item.Address ?? string.Empty;
        entity.Description = item.Description ?? string.Empty; entity.Latitude = item.Latitude; entity.Longitude = item.Longitude;
        entity.PriceMin = item.PriceMin; entity.PriceMax = item.PriceMax; entity.IsFree = item.IsFree; entity.AgeLimit = item.AgeLimit;
        entity.Language = item.Language; entity.OrgLink = item.OrgLink?.AbsoluteUri; entity.ImageUrl = item.ImageUrl?.AbsoluteUri;
        entity.SourceStatus = item.SourceStatus; entity.Confidence = normalized.Confidence;
        entity.IssuesJson = ParserJson.Serialize(item.Issues); entity.DataHash = normalized.DataHash; entity.SourcePriority = source.Priority;
        entity.SourceType = source.SourceType;
        entity.Status = item.SourceStatus == SourceEventStatus.Cancelled ? PublicationStatus.Cancelled :
            entity.ManuallySuppressed ? suppressedStatus :
            source.SourceType == EventSourceType.Page && !item.RequiresReview && item.Issues.Count == 0 && normalized.Confidence >= 0.9m ? PublicationStatus.Published : PublicationStatus.Draft;
        entity.VerifiedAt = checkedAt; entity.UpdatedAt = checkedAt;
        if (!entity.ManuallySuppressed) { entity.ReviewedAt = null; entity.ReviewNote = null; }
        entity.Version++;
    }

}
