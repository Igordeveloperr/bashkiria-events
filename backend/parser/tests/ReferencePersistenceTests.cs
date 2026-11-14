using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.References;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Parser.Tests;

public sealed class ReferencePersistenceTests : IAsyncLifetime
{
    private readonly string _prefix = "reference-test-" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _cities = [];
    private readonly List<Guid> _documents = [];
    private readonly TimeProvider _clock = TimeProvider.System;
    private PooledDbContextFactory<ParserDbContext> Factory => new(new DbContextOptionsBuilder<ParserDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING")).Options);
    private ReferenceCatalog Catalog => new(Factory, _clock);
    private ReferenceEventNormalizer References => new(Factory, new(_clock), new VenueResolver(), _clock);
    private EfEventStore Store => new(Factory, new(_clock), new VenueResolver(), References);

    [PostgresFact]
    public async Task AliasNormalizesCityVenueAndAddressAndDeduplicatesAcrossSources()
    {
        var city = await CityAsync("Первый");
        var venue = await Catalog.CreateVenueAsync(new(city, "Концертный зал", "ул. Ленина, 50"), default);
        await Catalog.AddCityAliasAsync(city, new("Городской округ " + _prefix), default);
        await Catalog.AddVenueAliasAsync(venue, new("КЗ"), default);
        var first = await SaveAsync(Candidate() with { City = "город " + _prefix + "Первый", Venue = "Концертный зал", Address = "улица Ленина, 50" }, "first");
        var second = await SaveAsync(Candidate() with { City = "Городской округ " + _prefix, Venue = "КЗ", Address = null }, "second");
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(PublicationStatus.Published, first.Status);
        Assert.Equal(PublicationStatus.Published, second.Status);
        await using var db = await Factory.CreateDbContextAsync();
        var stored = await db.Events.SingleAsync(item => item.Id == first.EventId);
        Assert.Equal(venue, stored.VenueId);
        Assert.Equal(city, stored.CityId);
        Assert.Equal("Концертный зал", stored.Venue);
        Assert.Equal("ул. Ленина, 50", stored.Address);
        Assert.Equal(2, await db.EventSources.CountAsync(item => item.EventId == first.EventId));
    }

    [PostgresFact]
    public async Task SameVenueNameInDifferentCitiesAndBranchesNeverMerges()
    {
        var city1 = await CityAsync("Первый");
        var city2 = await CityAsync("Второй");
        var a = await Catalog.CreateVenueAsync(new(city1, "ДК", "ул. Ленина, 1"), default);
        var b = await Catalog.CreateVenueAsync(new(city1, "ДК", "ул. Ленина, 2"), default);
        var c = await Catalog.CreateVenueAsync(new(city2, "ДК", "ул. Ленина, 1"), default);
        var first = await SaveAsync(Candidate() with { CityId = city1, Venue = "ДК", Address = "ул. Ленина, 1" }, "first");
        var second = await SaveAsync(Candidate() with { CityId = city1, Venue = "ДК", Address = "ул. Ленина, 2" }, "second");
        var third = await SaveAsync(Candidate() with { CityId = city2, Venue = "ДК", Address = "ул. Ленина, 1" }, "third");
        Assert.Equal(3, new[] { first.EventId, second.EventId, third.EventId }.Distinct().Count());
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(3, await db.Events.CountAsync(item => item.VenueId == a || item.VenueId == b || item.VenueId == c));
        var unresolved = await SaveAsync(Candidate() with { CityId = city1, Venue = "ДК", Address = null }, "ambiguous");
        Assert.Equal(PublicationStatus.Draft, unresolved.Status);
        Assert.Contains(unresolved.Issues!, issue => issue.Code == "ambiguous_venue");
    }

    [PostgresFact]
    public async Task AddressMismatchStaysDraftUntilAnExplicitAddressAliasIsAdded()
    {
        var city = await CityAsync("Первый");
        var venue = await Catalog.CreateVenueAsync(new(city, "Зал", "ул. Ленина, 1"), default);
        var candidate = Candidate() with { CityId = city, Venue = "Зал", Address = "Ленина, д. 1" };
        var saved = await SaveAsync(candidate, "same");
        Assert.Contains(saved.Issues!, issue => issue.Code == "venue_address_conflict");
        Assert.Equal(PublicationStatus.Draft, saved.Status);
        await Catalog.AddVenueAliasAsync(venue, new("Зал", "Ленина, д. 1"), default);
        var repeated = await SaveAsync(candidate, "same");
        Assert.Equal(saved.EventId, repeated.EventId);
        Assert.DoesNotContain(repeated.Issues!, issue => issue.Code == "venue_address_conflict");
        // Resolving a dictionary issue never silently publishes a previously withdrawn card.
        Assert.Equal(PublicationStatus.Draft, repeated.Status);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(venue, (await db.Events.SingleAsync(item => item.Id == saved.EventId)).VenueId);
    }

    [PostgresFact]
    public async Task UnknownNamesArePreservedAndInvalidExplicitIdsAreRejected()
    {
        var saved = await SaveAsync(Candidate() with { City = _prefix, Venue = "Неизвестная площадка" }, "unknown");
        Assert.Equal(PublicationStatus.Draft, saved.Status);
        Assert.Contains(saved.Issues!, issue => issue.Code == "unknown_city");
        await using var db = await Factory.CreateDbContextAsync();
        var stored = await db.Events.SingleAsync(item => item.Id == saved.EventId);
        Assert.Null(stored.CityId);
        Assert.Null(stored.VenueId);
        Assert.Equal("Неизвестная площадка", stored.Venue);
        await Assert.ThrowsAsync<ParserValidationException>(() => SaveAsync(Candidate() with { VenueId = Guid.NewGuid() }, "invalid"));
        var city1 = await CityAsync("Первый");
        var city2 = await CityAsync("Второй");
        var venue = await Catalog.CreateVenueAsync(new(city1, "Зал", "ул. Ленина, 1"), default);
        await Assert.ThrowsAsync<ParserValidationException>(() => SaveAsync(Candidate() with { CityId = city2, VenueId = venue }, "mismatch"));
    }

    [PostgresFact]
    public async Task CatalogUpdateAndBackfillPreserveManualEditsVerificationAndSourceHashes()
    {
        var city = await CityAsync("Первый");
        var original = Candidate() with { CityId = city, Venue = "Сокращение", Address = "ул. Ленина, 1" };
        var saved = await SaveAsync(original, "original");
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.Events.SingleAsync(item => item.Id == saved.EventId);
        item.Title = "Исправленное название"; item.NormalizedTitle = CandidateNormalizer.NormalizeText(item.Title);
        item.ReviewedAt = _clock.GetUtcNow(); item.ReviewNote = "Проверено";
        await db.SaveChangesAsync();
        var hash = item.DataHash; var verified = item.VerifiedAt;
        var venue = await Catalog.CreateVenueAsync(new(city, "Полное название", "ул. Ленина, 1"), default);
        await Catalog.AddVenueAliasAsync(venue, new("Сокращение"), default);
        Assert.True(await References.ApplyAsync(db, item, default));
        await db.SaveChangesAsync();
        Assert.Equal("Исправленное название", item.Title);
        Assert.Equal(hash, item.DataHash);
        Assert.Equal(verified, item.VerifiedAt);
        Assert.Equal(venue, item.VenueId);
        Assert.False(await References.ApplyAsync(db, item, default));
        var replay = await SaveAsync(original, "original");
        Assert.Equal(saved.EventId, replay.EventId);
        await db.Entry(item).ReloadAsync();
        Assert.Equal("Исправленное название", item.Title);
        Assert.Equal(1, await db.AuditEntries.CountAsync(entry => entry.EventId == item.Id && entry.Action == "references_normalized"));
    }

    [PostgresFact]
    public async Task AliasCannotBeAssignedToTwoCitiesOrTwoVenues()
    {
        var city1 = await CityAsync("Первый");
        var city2 = await CityAsync("Второй");
        await Catalog.AddCityAliasAsync(city1, new(_prefix + "Алиас"), default);
        await Assert.ThrowsAsync<DbUpdateException>(() => Catalog.AddCityAliasAsync(city2, new(_prefix + "Алиас"), default));
        var first = await Catalog.CreateVenueAsync(new(city1, "Первый зал", "ул. Ленина, 1"), default);
        var second = await Catalog.CreateVenueAsync(new(city1, "Второй зал", "ул. Ленина, 2"), default);
        await Catalog.AddVenueAliasAsync(first, new("Сокращение"), default);
        await Assert.ThrowsAsync<DbUpdateException>(() => Catalog.AddVenueAliasAsync(second, new("Сокращение"), default));
    }

    private async Task<Guid> CityAsync(string suffix)
    {
        var city = await Catalog.CreateCityAsync(new(_prefix + suffix), default);
        _cities.Add(city);
        return city;
    }

    [PostgresFact]
    public async Task BackfillReportsExistingDuplicatesWithoutLosingSourcesOrReplacingCards()
    {
        var city = await CityAsync("Первый");
        var original = Candidate() with { CityId = city, Venue = "Алиас", Address = "ул. Ленина, 1" };
        var canonical = await SaveAsync(original with { Venue = "Полное название" }, "first");
        var alias = await SaveAsync(original, "second");
        Assert.NotEqual(canonical.EventId, alias.EventId);
        var venue = await Catalog.CreateVenueAsync(new(city, "Полное название", "ул. Ленина, 1"), default);
        await Catalog.AddVenueAliasAsync(venue, new("Алиас"), default);
        await using var db = await Factory.CreateDbContextAsync();
        var first = await db.Events.SingleAsync(item => item.Id == canonical.EventId);
        var second = await db.Events.SingleAsync(item => item.Id == alias.EventId);
        await References.ApplyAsync(db, first, default);
        await db.SaveChangesAsync();
        await References.ApplyAsync(db, second, default);
        await db.SaveChangesAsync();
        Assert.Contains(ParserJson.Deserialize<List<ParsingIssue>>(second.IssuesJson)!, issue => issue.Code == "reference_duplicate");
        Assert.Equal(PublicationStatus.Draft, second.Status);
        Assert.Equal(2, await db.EventSources.CountAsync(item => item.EventId == first.Id || item.EventId == second.Id));
    }

    private EventCandidate Candidate() => new()
    {
        Title = _prefix, SourceUrl = new("https://example.org/" + _prefix), StartDate = new(2036, 11, 15), StartTime = new(17, 0),
        City = "Уфа", Venue = "ГКЗ «Башкортостан»", Address = "ул. Ленина, 50", PriceMin = 100, AgeLimit = 6, SourceStatus = SourceEventStatus.Scheduled
    };

    private async Task<EventSaveResult> SaveAsync(EventCandidate candidate, string suffix)
    {
        var id = Guid.NewGuid(); _documents.Add(id);
        var source = _prefix + suffix;
        var document = new RawDocument(id, source, candidate.SourceUrl, _clock.GetUtcNow(), 200, "fixture", CandidateNormalizer.Hash("fixture"));
        await new EfRawDocumentStore(Factory).SaveAsync(document, default);
        return await Store.SaveAsync(new(source, 100), candidate, document, default);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING"))) return;
        await using var db = await Factory.CreateDbContextAsync();
        var eventIds = await db.EventSources.Where(item => item.SourceId.StartsWith(_prefix)).Select(item => item.EventId).ToListAsync();
        await db.AuditEntries.Where(item => eventIds.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.EventSources.Where(item => eventIds.Contains(item.EventId)).ExecuteDeleteAsync();
        await db.Events.Where(item => eventIds.Contains(item.Id)).ExecuteDeleteAsync();
        await db.RawDocuments.Where(item => _documents.Contains(item.Id)).ExecuteDeleteAsync();
        await db.VenueAliases.Where(item => _cities.Contains(item.CityId)).ExecuteDeleteAsync();
        await db.Venues.Where(item => _cities.Contains(item.CityId)).ExecuteDeleteAsync();
        await db.CityAliases.Where(item => _cities.Contains(item.CityId)).ExecuteDeleteAsync();
        await db.Cities.Where(item => _cities.Contains(item.Id)).ExecuteDeleteAsync();
    }
}
