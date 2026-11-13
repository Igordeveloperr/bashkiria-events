using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Jobs;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Parser.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING")))
            Skip = "Set PARSER_TEST_CONNECTION_STRING to a migrated disposable PostgreSQL schema.";
    }
}

public sealed class PostgresPersistenceTests : IAsyncLifetime
{
    private readonly string _sourceId = "test-" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _documents = [];
    private readonly List<Guid> _events = [];
    private readonly TimeProvider _clock = TimeProvider.System;
    private PooledDbContextFactory<ParserDbContext> Factory => new(new DbContextOptionsBuilder<ParserDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING")).Options);
    private SourceOptions Source => new() { Id = _sourceId, Priority = 100 };
    private EfEventStore Store => new(Factory, new CandidateNormalizer(_clock));

    [PostgresFact]
    public async Task RawDocumentUsesExistingSchemaAndRetryIsIdempotent()
    {
        var document = await DocumentAsync();
        await new EfRawDocumentStore(Factory).SaveAsync(document, CancellationToken.None);
        await using var db = await Factory.CreateDbContextAsync();
        var stored = await db.RawDocuments.SingleAsync(item => item.Id == document.Id);
        Assert.Equal(document with { FetchedAt = stored.FetchedAt }, stored);
        Assert.InRange(Math.Abs((stored.FetchedAt - document.FetchedAt).Ticks), 0, 9);
        Assert.Equal(1, await db.RawDocuments.CountAsync(item => item.Id == document.Id));
    }

    [PostgresFact]
    public async Task RepeatUpdateAndCancellationKeepOneEventAndItsIdentity()
    {
        var first = await SaveAsync(Candidate());
        var repeated = await SaveAsync(Candidate());
        var moved = await SaveAsync(Candidate() with { StartTime = new TimeOnly(19, 0) });
        var cancelled = await SaveAsync(Candidate() with { StartTime = new TimeOnly(19, 0), SourceStatus = SourceEventStatus.Cancelled });
        Assert.Equal(first.EventId, repeated.EventId);
        Assert.Equal(first.EventId, moved.EventId);
        Assert.Equal(first.EventId, cancelled.EventId);
        Assert.Equal("unchanged", repeated.Outcome);
        Assert.Equal(PublicationStatus.Cancelled, cancelled.Status);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.EventSources.CountAsync(item => item.SourceId == _sourceId));
        Assert.Equal(new TimeOnly(19, 0), (await db.Events.SingleAsync(item => item.Id == first.EventId)).StartTime);
    }

    [PostgresFact]
    public async Task CrossSourceDeduplicationPreservesProvenanceAndHigherPriorityFields()
    {
        var candidate = Candidate();
        var first = await SaveAsync(candidate);
        var document = await DocumentAsync(_sourceId + "-second");
        var second = await Store.SaveAsync(new SourceOptions { Id = _sourceId + "-second", Priority = 50 },
            candidate with { SourceUrl = new Uri("https://example.org/events/second/"), Description = "Lower priority" }, document, CancellationToken.None);
        _events.Add(second.EventId);
        Assert.Equal(first.EventId, second.EventId);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.EventSources.CountAsync(item => item.EventId == first.EventId));
        Assert.NotEqual("Lower priority", (await db.Events.SingleAsync(item => item.Id == first.EventId)).Description);
    }

    [PostgresFact]
    public async Task ManualWithdrawalSurvivesSourceUpdatesAndVersionConflictsAreRejected()
    {
        var saved = await SaveAsync(Candidate());
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.Events.AsNoTracking().SingleAsync(item => item.Id == saved.EventId);
        var review = new EventReviewService(Factory, new CandidateNormalizer(_clock), _clock);
        await review.ReviewAsync(item.Id, new(item.Version, PublicationStatus.Cancelled, "Fixture moderator withdrawal"), CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewAsync(item.Id,
            new(item.Version, PublicationStatus.Published, "Stale version"), CancellationToken.None));
        await SaveAsync(Candidate() with { Description = "Changed source description" });
        var updated = await db.Events.AsNoTracking().SingleAsync(entity => entity.Id == item.Id);
        Assert.Equal(PublicationStatus.Cancelled, updated.Status);
        Assert.True(updated.ManuallySuppressed);
    }

    [PostgresFact]
    public async Task ConcurrentUpsertsDoNotDuplicateEvents()
    {
        var firstDocument = await DocumentAsync();
        var secondDocument = await DocumentAsync();
        var candidate = Candidate();
        var results = await Task.WhenAll(Store.SaveAsync(Source, candidate, firstDocument, CancellationToken.None),
            Store.SaveAsync(Source, candidate, secondDocument, CancellationToken.None));
        _events.AddRange(results.Select(item => item.EventId));
        Assert.Equal(results[0].EventId, results[1].EventId);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.EventSources.CountAsync(item => item.SourceId == _sourceId));
    }

    [PostgresFact]
    public async Task DurableQueueHasOneActiveRunAndOneClaimOwner()
    {
        var runs = new CollectionRunStore(Factory, _clock);
        var queued = await Task.WhenAll(runs.EnqueueAsync(_sourceId, 3, null, CancellationToken.None),
            runs.EnqueueAsync(_sourceId, 3, null, CancellationToken.None));
        Assert.Equal(queued[0].Id, queued[1].Id);
        var firstOwner = Guid.NewGuid(); var secondOwner = Guid.NewGuid();
        var claims = await Task.WhenAll(runs.ClaimNextAsync(firstOwner, CancellationToken.None), runs.ClaimNextAsync(secondOwner, CancellationToken.None));
        Assert.Single(claims, claim => claim is not null);
        var owner = claims[0] is not null ? firstOwner : secondOwner;
        await Assert.ThrowsAsync<RunLeaseLostException>(() => runs.HeartbeatAsync(queued[0].Id, Guid.NewGuid(), CancellationToken.None));
        await runs.HeartbeatAsync(queued[0].Id, owner, CancellationToken.None);
        await runs.FinishAsync(queued[0].Id, owner, null, "Fixture failure", CancellationToken.None);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(CollectionRunStatus.Failed, (await db.CollectionRuns.SingleAsync(run => run.Id == queued[0].Id)).Status);
    }

    [PostgresFact]
    public async Task ManualTimeCorrectionSurvivesAnUnchangedSource()
    {
        var saved = await SaveAsync(Candidate());
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.Events.AsNoTracking().SingleAsync(item => item.Id == saved.EventId);
        var review = new EventReviewService(Factory, new CandidateNormalizer(_clock), _clock);
        await review.ReviewAsync(item.Id, new(item.Version, PublicationStatus.Published,
            "Fixture confirmed new start time", StartTime: new TimeOnly(19, 0)), CancellationToken.None);

        var repeated = await SaveAsync(Candidate());

        Assert.Equal(saved.EventId, repeated.EventId);
        Assert.Equal("unchanged", repeated.Outcome);
        var updated = await db.Events.AsNoTracking().SingleAsync(entity => entity.Id == saved.EventId);
        Assert.Equal(new TimeOnly(19, 0), updated.StartTime);
        Assert.NotNull(updated.ReviewedAt);
        Assert.Equal(PublicationStatus.Published, updated.Status);
    }

    [PostgresFact]
    public async Task ExpiredLeaseIsRecoveredAndCancellationRevokesOwnership()
    {
        var runs = new CollectionRunStore(Factory, _clock);
        var queued = await runs.EnqueueAsync(_sourceId, 3, null, CancellationToken.None);
        var firstOwner = Guid.NewGuid(); var secondOwner = Guid.NewGuid();
        await runs.ClaimNextAsync(firstOwner, CancellationToken.None);
        await using var db = await Factory.CreateDbContextAsync();
        await db.CollectionRuns.Where(run => run.Id == queued.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

        var recovered = await runs.ClaimNextAsync(secondOwner, CancellationToken.None);

        Assert.Equal(queued.Id, recovered?.Id);
        await Assert.ThrowsAsync<RunLeaseLostException>(() => runs.HeartbeatAsync(queued.Id, firstOwner, CancellationToken.None));
        Assert.True(await runs.CancelAsync(queued.Id, CancellationToken.None));
        await Assert.ThrowsAsync<RunLeaseLostException>(() => runs.HeartbeatAsync(queued.Id, secondOwner, CancellationToken.None));
        var current = await db.CollectionRuns.AsNoTracking().SingleAsync(run => run.Id == queued.Id);
        Assert.Equal(CollectionRunStatus.Cancelled, current.Status);
        Assert.Equal(2, current.Attempts);
    }

    [PostgresFact]
    public async Task CompletedScheduleWindowIsNotEnqueuedAgain()
    {
        var runs = new CollectionRunStore(Factory, _clock);
        var first = await runs.EnqueueAsync(_sourceId, 3, _sourceId + ":window", CancellationToken.None);
        var owner = Guid.NewGuid();
        await runs.ClaimNextAsync(owner, CancellationToken.None);
        await runs.FinishAsync(first.Id, owner, null, "Fixture completed attempt", CancellationToken.None);
        var repeated = await runs.EnqueueAsync(_sourceId, 3, _sourceId + ":window", CancellationToken.None);
        Assert.Equal(first.Id, repeated.Id);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.CollectionRuns.CountAsync(item => item.SourceId == _sourceId));
    }

    private EventCandidate Candidate() => new()
    {
        Title = _sourceId,
        SourceUrl = new Uri($"https://example.org/events/{_sourceId}/"),
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        StartTime = new TimeOnly(17, 0),
        City = "Уфа",
        Venue = "Fixture venue",
        Address = "Fixture address",
        Description = "Fixture description",
        PriceMin = 100,
        PriceMax = 100,
        AgeLimit = 6,
        SourceStatus = SourceEventStatus.Scheduled
    };

    private async Task<RawDocument> DocumentAsync(string? sourceId = null)
    {
        var document = RawDocument.Create(sourceId ?? _sourceId, Candidate().SourceUrl, 200, "<h1>Fixture</h1>");
        await new EfRawDocumentStore(Factory).SaveAsync(document, CancellationToken.None);
        _documents.Add(document.Id);
        return document;
    }

    private async Task<EventSaveResult> SaveAsync(EventCandidate candidate)
    {
        var result = await Store.SaveAsync(Source, candidate, await DocumentAsync(), CancellationToken.None);
        _events.Add(result.EventId);
        return result;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING"))) return;
        await using var db = await Factory.CreateDbContextAsync();
        _events.AddRange(await db.EventSources.Where(item => item.SourceId.StartsWith(_sourceId))
            .Select(item => item.EventId).Distinct().ToListAsync());
        await db.CollectionRuns.Where(item => item.SourceId == _sourceId).ExecuteDeleteAsync();
        await db.AuditEntries.Where(item => _events.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.EventSources.Where(item => _events.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.Events.Where(item => _events.Contains(item.Id)).ExecuteDeleteAsync();
        await db.RawDocuments.Where(item => _documents.Contains(item.Id)).ExecuteDeleteAsync();
    }
}
