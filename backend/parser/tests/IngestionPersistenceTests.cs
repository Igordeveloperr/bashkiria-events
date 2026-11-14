using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Ingestion;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Bashkiria.Events.Parser.References;

namespace Parser.Tests;

public sealed class IngestionPersistenceTests : IAsyncLifetime
{
    private readonly string _dataset = "fixture-" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _manualDocuments = [];
    private readonly List<Guid> _pageDocuments = [];
    private readonly AdjustableClock _clock = new();
    private PooledDbContextFactory<ParserDbContext> Factory => new(new DbContextOptionsBuilder<ParserDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING")).Options);
    private EventIngestionService Service => new(new EventInputReader(), new EventInputMapper(new(_clock)), new(_clock),
        new EfRawDocumentStore(Factory), Store, Factory, Options.Create(new IngestionOptions()), _clock);
    private EfEventStore Store => new(Factory, new(_clock), new VenueResolver(),
        new ReferenceEventNormalizer(Factory, new(_clock), new VenueResolver(), _clock));

    [PostgresFact]
    public async Task HundredEventImportRepeatsWithoutDuplicatesAndUpdatesByExternalId()
    {
        var input = Enumerable.Range(0, 100).Select(index => Input(index)).ToArray();
        var text = ParserJson.Serialize(input);
        var first = await Service.ImportAsync(_dataset, text, "application/json", CancellationToken.None);
        var second = await Service.ImportAsync(_dataset, text, "application/json", CancellationToken.None);
        Assert.Equal(100, first.Added);
        Assert.Equal(100, second.Unchanged);
        Assert.Equal(first.RawDocumentId, second.RawDocumentId);
        Assert.All(first.Items, item => Assert.Equal(PublicationStatus.Draft, item.Status));

        _clock.Now = _clock.Now.AddHours(1);
        input[0] = input[0] with { Description = "Corrected description", StartTime = new TimeOnly(19, 0) };
        var updated = await Service.ImportAsync(_dataset, ParserJson.Serialize(input), "application/json", CancellationToken.None);
        Assert.Equal(1, updated.Updated);
        Assert.Equal(99, updated.Unchanged);
        Assert.Equal(first.Items[0].EventId, updated.Items[0].EventId);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(100, await db.EventSources.CountAsync(source => source.SourceId == "import:" + _dataset));
        Assert.Equal(new TimeOnly(19, 0), (await db.Events.SingleAsync(item => item.Id == first.Items[0].EventId)).StartTime);
        var raw = await db.RawDocuments.SingleAsync(document => document.Id == first.RawDocumentId);
        Assert.Null(raw.HttpStatus);
        Assert.Equal(EventSourceType.Import, raw.SourceType);
        Assert.Equal("application/json", raw.ContentType);
        Assert.Equal("urn", raw.Url.Scheme);
        Assert.Equal(text, raw.Content);

        var replay = await Service.ReplayAsync(raw, CancellationToken.None);
        Assert.Equal(100, replay.Unchanged);
        Assert.Equal(new TimeOnly(19, 0), (await db.Events.AsNoTracking().SingleAsync(item => item.Id == first.Items[0].EventId)).StartTime);
    }

    [PostgresFact]
    public async Task CsvAndJsonUseTheSameIdentityEvenWhenRowsAreReordered()
    {
        var input = Input(0);
        var json = await Service.ImportAsync(_dataset, ParserJson.Serialize(new[] { input }), "application/json", CancellationToken.None);
        _clock.Now = _clock.Now.AddMinutes(1);
        var csv = $"external_id;title;city;venue;address;start_date;start_time;price_min;price_max;age_limit;source_status\n" +
            $"event-0;{input.Title};{input.City};{input.Venue};{input.Address};2036-11-15;17:00:00;100;100;6;scheduled\n";
        var imported = await Service.ImportAsync(_dataset, csv, "text/csv", CancellationToken.None);
        Assert.Equal(0, imported.Added);
        Assert.Equal(json.Items[0].EventId, imported.Items[0].EventId);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.EventSources.CountAsync(item => item.SourceId == "import:" + _dataset));
    }

    [PostgresFact]
    public async Task PartialImportReportsEveryRejectedRecordAndRetainsOriginalDocument()
    {
        var valid = ParserJson.Serialize(Input(0));
        var invalidPrice = ParserJson.Serialize(Input(1) with { PriceMin = -1 });
        var duplicate = ParserJson.Serialize(Input(2) with { ExternalId = "event-0" });
        var text = $"[{valid},{{\"title\":null}},{invalidPrice},{duplicate}]";
        var result = await Service.ImportAsync(_dataset, text, "application/json", CancellationToken.None);
        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.Rejected);
        Assert.Equal(1, result.RequiresReview);
        Assert.Contains(result.Items[1].Errors, error => error.Field == "title");
        Assert.Contains(result.Items[2].Errors, error => error.Field == "priceMin");
        Assert.Contains(result.Items[3].Errors, error => error.Code == "duplicate_external_id");
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(text, (await db.RawDocuments.SingleAsync(item => item.Id == result.RawDocumentId)).Content);
    }

    [PostgresFact]
    public async Task ManualCreationIsIdempotentAndConflictingPayloadCannotChangeIt()
    {
        var input = Input(0) with { ExternalId = null };
        var key = Guid.NewGuid();
        var first = await Service.CreateAsync(ParserJson.Serialize(input), key, CancellationToken.None);
        _manualDocuments.Add(first.RawDocumentId);
        var repeated = await Service.CreateAsync(" \n" + ParserJson.Serialize(input) + "\n", key, CancellationToken.None);
        Assert.Equal(first.EventId, repeated.EventId);
        Assert.Equal("unchanged", repeated.Outcome);
        Assert.Equal(PublicationStatus.Draft, first.Status);
        var conflict = await Assert.ThrowsAsync<IngestionRequestException>(() => Service.CreateAsync(
            ParserJson.Serialize(input with { Title = "Changed payload" }), key, CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.Events.SingleAsync(item => item.Id == first.EventId);
        Assert.Equal(input.Title, item.Title);
        Assert.Equal(EventSourceType.Manual, item.SourceType);
        Assert.Contains(await db.AuditEntries.Where(entry => entry.EventId == item.Id).ToListAsync(),
            entry => entry.Action == "created_manually" && entry.Actor == "api-key");
    }

    [PostgresFact]
    public async Task ConcurrentManualRequestsProduceOneDocumentAndOneEvent()
    {
        var input = Input(0) with { ExternalId = null };
        var text = ParserJson.Serialize(input);
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(Service.CreateAsync(text, key, CancellationToken.None), Service.CreateAsync(text, key, CancellationToken.None));
        _manualDocuments.AddRange(results.Select(result => result.RawDocumentId));
        Assert.Equal(results[0].EventId, results[1].EventId);
        Assert.Equal(results[0].RawDocumentId, results[1].RawDocumentId);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.RawDocuments.CountAsync(item => item.Id == results[0].RawDocumentId));
        Assert.Equal(1, await db.EventSources.CountAsync(item => item.EventId == results[0].EventId));
    }

    [PostgresFact]
    public async Task ImportCannotOverwriteOrWithdrawAnExistingOfficialCard()
    {
        var input = Input(0);
        var candidate = new EventCandidate
        {
            Title = input.Title!, SourceUrl = new Uri("https://example.org/events/" + _dataset),
            StartDate = input.StartDate, StartTime = input.StartTime, City = input.City, Venue = input.Venue,
            Address = input.Address, Description = "Official description", PriceMin = 100, PriceMax = 100,
            AgeLimit = 6, SourceStatus = SourceEventStatus.Scheduled
        };
        var document = RawDocument.Create(_dataset, candidate.SourceUrl, 200, "<h1>Fixture</h1>");
        _pageDocuments.Add(document.Id);
        await new EfRawDocumentStore(Factory).SaveAsync(document, CancellationToken.None);
        var official = await Store.SaveAsync(new(_dataset, 100), candidate, document, CancellationToken.None);
        var imported = await Service.ImportAsync(_dataset, ParserJson.Serialize(new[] { input with
        { Description = "Untrusted cancellation", SourceStatus = SourceEventStatus.Cancelled } }), "application/json", CancellationToken.None);
        Assert.Equal(official.EventId, imported.Items[0].EventId);
        Assert.Equal("unchanged", imported.Items[0].Outcome);
        await using var db = await Factory.CreateDbContextAsync();
        var stored = await db.Events.SingleAsync(item => item.Id == official.EventId);
        Assert.Equal(PublicationStatus.Published, stored.Status);
        Assert.Equal("Official description", stored.Description);
        Assert.Equal(EventSourceType.Page, stored.SourceType);
    }

    [PostgresFact]
    public async Task ManualModerationAndReplayPreserveCorrectionsAndVerificationTime()
    {
        var created = await Service.CreateAsync(ParserJson.Serialize(Input(0) with { ExternalId = null }), Guid.NewGuid(), CancellationToken.None);
        _manualDocuments.Add(created.RawDocumentId);
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.Events.AsNoTracking().SingleAsync(item => item.Id == created.EventId);
        _clock.Now = _clock.Now.AddHours(2);
        await new EventReviewService(Factory, new(_clock), _clock, new VenueResolver()).ReviewAsync(item.Id,
            new(item.Version, PublicationStatus.Published, "Fixture moderator confirmation", Title: "Confirmed title"), CancellationToken.None);
        var raw = await db.RawDocuments.SingleAsync(item => item.Id == created.RawDocumentId);
        var replay = await Service.ReplayAsync(raw, CancellationToken.None);
        Assert.Equal(1, replay.Unchanged);
        var stored = await db.Events.AsNoTracking().SingleAsync(item => item.Id == created.EventId);
        Assert.Equal("Confirmed title", stored.Title);
        Assert.Equal(PublicationStatus.Published, stored.Status);
        Assert.Equal(_clock.Now, stored.VerifiedAt);
    }

    private EventInput Input(int number) => new()
    {
        ExternalId = "event-" + number, Title = _dataset + "/" + number,
        City = "Уфа", Venue = "ГКЗ «Башкортостан»", Address = "ул. Ленина, 50", StartDate = new(2036, 11, 15), StartTime = new(17, 0),
        PriceMin = 100, PriceMax = 100, AgeLimit = 6, SourceStatus = SourceEventStatus.Scheduled
    };

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING"))) return;
        await using var db = await Factory.CreateDbContextAsync();
        var documents = await db.RawDocuments.Where(item => item.SourceId == "import:" + _dataset ||
            _manualDocuments.Contains(item.Id) || _pageDocuments.Contains(item.Id)).Select(item => item.Id).ToListAsync();
        var ids = await db.EventSources.Where(item => documents.Contains(item.RawDocumentId)).Select(item => item.EventId).Distinct().ToListAsync();
        await db.AuditEntries.Where(item => ids.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.EventSources.Where(item => ids.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.Events.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
        await db.RawDocuments.Where(item => documents.Contains(item.Id)).ExecuteDeleteAsync();
    }

    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 7, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
