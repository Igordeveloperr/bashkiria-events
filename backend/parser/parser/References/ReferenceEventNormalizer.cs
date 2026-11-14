using System.Text.Json.Nodes;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Parser.References;

public sealed class ReferenceEventNormalizer(IDbContextFactory<ParserDbContext> contexts,
    CandidateNormalizer normalizer, VenueResolver resolver, TimeProvider clock)
{
    public async Task<PageResponse<ReferenceNormalizationResult>> NormalizePageAsync(int page, int pageSize, CancellationToken token)
    {
        await using var listing = await contexts.CreateDbContextAsync(token);
        var count = await listing.Events.CountAsync(token);
        // No records are removed, so page boundaries stay stable during normalization.
        var ids = await listing.Events.AsNoTracking().OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(item => item.Id).ToListAsync(token);
        var results = new List<ReferenceNormalizationResult>();
        foreach (var id in ids)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    results.Add(await NormalizeOneAsync(id, token));
                    break;
                }
                catch (Exception exception) when (attempt < 5 && DatabaseErrors.IsConflict(exception))
                {
                    await Task.Delay(50 * (1 << attempt) + Random.Shared.Next(25, 76), token);
                }
            }
        }
        return new(count, page, pageSize, results);
    }

    private async Task<ReferenceNormalizationResult> NormalizeOneAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        var item = await db.Events.SingleAsync(item => item.Id == id, token);
        var changed = await ApplyAsync(db, item, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        var issues = ParserJson.Deserialize<List<ParsingIssue>>(item.IssuesJson) ?? [];
        return new(id, changed ? "updated" : "unchanged", issues.Select(issue => issue.Code).ToArray());
    }

    public async Task<bool> ApplyAsync(ParserDbContext db, ParsedEvent entity, CancellationToken token)
    {
        var before = Snapshot(entity);
        var candidate = await resolver.ResolveAsync(db, FromEvent(entity), token);
        var normalized = normalizer.Normalize("reference", candidate);
        var issues = normalized.Candidate.Issues.ToList();
        var duplicate = await db.Events.AnyAsync(item => item.Id != entity.Id && item.DedupKey == normalized.DedupKey, token);
        if (duplicate) issues.Add(new("reference_duplicate", "После нормализации найдена другая карточка этого сеанса. Требуется разбор дублей."));
        entity.CityId = candidate.CityId; entity.VenueId = candidate.VenueId;
        entity.City = candidate.City ?? string.Empty; entity.Venue = candidate.Venue ?? string.Empty; entity.Address = candidate.Address ?? string.Empty;
        if (!duplicate && candidate.StartDate.HasValue && candidate.StartTime.HasValue &&
            !string.IsNullOrWhiteSpace(candidate.City) && !string.IsNullOrWhiteSpace(candidate.Venue) &&
            (candidate.VenueId.HasValue || !string.IsNullOrWhiteSpace(candidate.Address))) entity.DedupKey = normalized.DedupKey;
        entity.IssuesJson = ParserJson.Serialize(issues);
        entity.Confidence = Math.Clamp(0.95m - issues.Count * 0.1m, 0.1m, 0.95m);
        if (issues.Count > 0 && entity.Status == PublicationStatus.Published) entity.Status = PublicationStatus.Draft;
        var after = Snapshot(entity);
        if (before == after) return false;
        entity.UpdatedAt = clock.GetUtcNow(); entity.Version++;
        // Catalog edits do not refresh source verification, replace content, or change source hashes.
        await db.AuditEntries.AddAsync(new EventAuditEntry
        {
            Id = Guid.NewGuid(), EventId = entity.Id, Action = "references_normalized", Actor = "reference-catalog",
            CreatedAt = clock.GetUtcNow(), SnapshotJson = ParserJson.Serialize(new { Before = before, After = after })
        }, token);
        return true;
    }

    private sealed record ReferenceSnapshot(Guid? CityId, Guid? VenueId, string City, string Venue, string Address,
        string DedupKey, PublicationStatus Status, decimal Confidence, string IssuesJson);

    private static ReferenceSnapshot Snapshot(ParsedEvent item) => new(item.CityId, item.VenueId, item.City, item.Venue,
        item.Address, item.DedupKey, item.Status, item.Confidence, ParserJson.Serialize(JsonNode.Parse(item.IssuesJson)));

    private static readonly HashSet<string> RecomputedIssues = ["missing_title", "missing_city", "missing_venue", "missing_address",
        "missing_start_date", "missing_start_time", "unknown_price", "unknown_age", "expired", "unconfirmed_status"];

    public static EventCandidate FromEvent(ParsedEvent item) => new()
    {
        Title = item.Title, SourceUrl = new Uri("urn:bashkiria-events:event:" + item.Id),
        CityId = item.CityId, VenueId = item.VenueId, City = item.City, Venue = item.Venue, Address = item.Address,
        StartDate = item.StartDate, StartTime = item.StartTime, EndDate = item.EndDate, EndTime = item.EndTime,
        Description = item.Description, Category = item.Category, Tags = item.Tags,
        PriceMin = item.PriceMin, PriceMax = item.PriceMax, IsFree = item.IsFree, AgeLimit = item.AgeLimit,
        Latitude = item.Latitude, Longitude = item.Longitude, Language = item.Language,
        OrgLink = item.OrgLink is null ? null : new Uri(item.OrgLink), ImageUrl = item.ImageUrl is null ? null : new Uri(item.ImageUrl),
        SourceStatus = item.SourceStatus,
        Issues = (ParserJson.Deserialize<List<ParsingIssue>>(item.IssuesJson) ?? [])
            .Where(issue => !RecomputedIssues.Contains(issue.Code) && !(item.ReviewedAt.HasValue && issue.Code == "input_requires_review")).ToArray()
    };
}
