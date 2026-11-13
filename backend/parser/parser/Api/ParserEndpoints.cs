using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Parsing;
using System.Text.Json.Nodes;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Jobs;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Api;

public static class ParserEndpoints
{
    public static void MapParserEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/parser").AddEndpointFilter<ParserApiKeyFilter>()
            .RequireRateLimiting("parser-api").WithTags("Parser");

        api.MapGet("/sources", (SourceRegistry sources) => Results.Ok(sources.Sources.Select(source => new
        { source.Id, source.Kind, source.Enabled, source.ListingUrl, source.Priority })));

        api.MapPost("/runs/{sourceId}", async (string sourceId, int? maxPages, SourceRegistry sources,
            CollectionRunStore runs, IOptions<ParserOptions> options, CancellationToken token) =>
        {
            var source = sources.Get(sourceId);
            var limit = maxPages ?? options.Value.MaxPagesPerRun;
            if (limit < 1 || limit > options.Value.MaxPagesPerRun) return Results.BadRequest(new { message = "Некорректный лимит карточек." });
            var run = await runs.EnqueueAsync(source.Id, limit, null, token);
            return Results.Accepted($"/api/v1/parser/runs/{run.Id}", RunDto(run));
        }).Produces<CollectionRunResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/runs", async (IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var runs = await db.CollectionRuns.AsNoTracking().OrderByDescending(run => run.CreatedAt).Take(50).ToListAsync(token);
            return Results.Ok(runs.Select(RunDto));
        }).Produces<IReadOnlyList<CollectionRunResponse>>();

        api.MapGet("/runs/{id:guid}", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var run = await db.CollectionRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == id, token);
            return run is null ? Results.NotFound() : Results.Ok(RunDto(run));
        }).Produces<CollectionRunResponse>();

        api.MapDelete("/runs/{id:guid}", async (Guid id, CollectionRunStore runs, CancellationToken token) =>
            await runs.CancelAsync(id, token) ? Results.NoContent() : Results.Conflict(new { message = "Запуск уже завершён или не существует." }));

        api.MapGet("/events", async (IDbContextFactory<ParserDbContext> contexts, int? page, int? pageSize,
            PublicationStatus? status, string? city, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 30;
            if (number < 1 || number > 100000 || size is < 1 or > 100 || status.HasValue && !Enum.IsDefined(status.Value))
                return Results.BadRequest(new { message = "Некорректные параметры списка." });
            await using var db = await contexts.CreateDbContextAsync(token);
            var query = db.Events.AsNoTracking().Where(item => !status.HasValue || item.Status == status)
                .Where(item => city == null || item.City == city);
            var count = await query.CountAsync(token);
            var items = await query.Include(item => item.Sources).OrderBy(item => item.StartDate).ThenBy(item => item.Id)
                .Skip((number - 1) * size).Take(size).ToListAsync(token);
            return Results.Ok(new PageResponse<EventResponse>(count, number, size, items.Select(EventDto).ToArray()));
        }).Produces<PageResponse<EventResponse>>();

        api.MapGet("/events/published", async (IDbContextFactory<ParserDbContext> contexts,
            IOptions<ParserOptions> options, TimeProvider clock, int? page, int? pageSize, CancellationToken token) =>
        {
            var number = page ?? 1; var size = pageSize ?? 30;
            if (number < 1 || number > 100000 || size is < 1 or > 100) return Results.BadRequest();
            await using var db = await contexts.CreateDbContextAsync(token);
            var freshSince = clock.GetUtcNow().AddHours(-options.Value.FreshnessHours);
            var now = clock.GetUtcNow();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), CandidateNormalizer.Zone).DateTime);
            var query = db.Events.AsNoTracking().Where(item => item.Status == PublicationStatus.Published &&
                item.SourceStatus != SourceEventStatus.Cancelled && item.SourceStatus != SourceEventStatus.Postponed &&
                item.VerifiedAt >= freshSince && (item.EndDate ?? item.StartDate) >= today &&
                (item.DateEnd >= now || item.DateEnd == null && (item.EndDate > item.StartDate || item.DateStart >= now)));
            var count = await query.CountAsync(token);
            var items = await query.Include(item => item.Sources).OrderBy(item => item.StartDate).ThenBy(item => item.Id)
                .Skip((number - 1) * size).Take(size).ToListAsync(token);
            return Results.Ok(new PageResponse<EventResponse>(count, number, size, items.Select(EventDto).ToArray()));
        }).Produces<PageResponse<EventResponse>>();

        api.MapGet("/events/{id:guid}", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var item = await db.Events.AsNoTracking().Include(item => item.Sources).SingleOrDefaultAsync(item => item.Id == id, token);
            return item is null ? Results.NotFound() : Results.Ok(EventDto(item));
        }).Produces<EventResponse>();

        api.MapPatch("/events/{id:guid}/review", async (Guid id, ReviewEventRequest request, EventReviewService review, CancellationToken token) =>
        {
            await review.ReviewAsync(id, request, token);
            return Results.NoContent();
        });

        api.MapGet("/events/{id:guid}/audit", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var entries = await db.AuditEntries.AsNoTracking().Where(item => item.EventId == id)
                .OrderByDescending(item => item.CreatedAt).Take(100).ToListAsync(token);
            return Results.Ok(entries.Select(entry => new { entry.Id, entry.Action, entry.Actor, entry.CreatedAt, snapshot = JsonNode.Parse(entry.SnapshotJson) }));
        });

        api.MapGet("/raw-documents/{id:guid}", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var document = await db.RawDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            return document is null ? Results.NotFound() : Results.Ok(document);
        });

        api.MapPost("/raw-documents/{id:guid}/reparse", async (Guid id, IDbContextFactory<ParserDbContext> contexts,
            SourceRegistry sources, IEventStore events, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var document = await db.RawDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
                ?? throw new KeyNotFoundException();
            if (document.HttpStatus is < 200 or > 299) return Results.BadRequest(new { message = "Нельзя разбирать неуспешный HTTP-ответ." });
            var source = sources.Get(document.SourceId);
            var candidates = sources.Parse(source, document.Content, document.Url);
            var results = new List<EventSaveResult>();
            foreach (var candidate in candidates)
                results.Add(await events.SaveAsync(source, candidate, document, token));
            return Results.Ok(new { rawDocumentId = id, results });
        });

        api.MapGet("/stats", async (IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var documents = await db.RawDocuments.CountAsync(token);
            var statuses = await db.Events.GroupBy(item => item.Status).Select(group => new { status = group.Key, count = group.Count() }).ToListAsync(token);
            var runs = await db.CollectionRuns.GroupBy(item => item.Status).Select(group => new { status = group.Key, count = group.Count() }).ToListAsync(token);
            return Results.Ok(new { rawDocuments = documents, events = statuses, runs });
        });
    }

    private static CollectionRunResponse RunDto(CollectionRun run) => new(
        run.Id, run.SourceId, run.Status, run.MaxPages, run.Attempts, run.CreatedAt, run.StartedAt,
        run.FinishedAt, run.Error, run.ReportJson is null ? null : ParserJson.Deserialize<CollectionResult>(run.ReportJson));

    private static EventResponse EventDto(ParsedEvent item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        NormalizedTitle = item.NormalizedTitle,
        Category = item.Category,
        Tags = item.Tags,
        StartDate = item.StartDate,
        StartTime = item.StartTime,
        EndDate = item.EndDate,
        EndTime = item.EndTime,
        DateStart = item.DateStart,
        DateEnd = item.DateEnd,
        DurationMin = item.DurationMin,
        City = item.City,
        Venue = item.Venue,
        Address = item.Address,
        Latitude = item.Latitude,
        Longitude = item.Longitude,
        Description = item.Description,
        PriceMin = item.PriceMin,
        PriceMax = item.PriceMax,
        IsFree = item.IsFree,
        AgeLimit = item.AgeLimit,
        Language = item.Language,
        OrgLink = item.OrgLink,
        ImageUrl = item.ImageUrl,
        Status = item.Status,
        SourceStatus = item.SourceStatus,
        Confidence = item.Confidence,
        Issues = ParserJson.Deserialize<List<ParsingIssue>>(item.IssuesJson) ?? [],
        RequiresReview = item.Status == PublicationStatus.Draft,
        VerifiedAt = item.VerifiedAt,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        ReviewedAt = item.ReviewedAt,
        ReviewNote = item.ReviewNote,
        Version = item.Version,
        SourceHash = item.DataHash,
        DedupKey = item.DedupKey,
        Sources = item.Sources.Select(source => new EventSourceResponse(source.SourceId, source.SourceUrl, source.RawDocumentId, source.CheckedAt)).ToArray()
    };
}
