extern alias Parser;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bashkiria.Events.Interaction.Common;
using Bashkiria.Events.Interaction.Configuration;
using Bashkiria.Events.Interaction.Contracts;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Models;
using Bashkiria.Events.Interaction.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using ParserDb = Parser::Bashkiria.Events.Parser.Data.ParserDbContext;
using ParserModels = Parser::Bashkiria.Events.Parser.Models;

namespace Interaction.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INTERACTION_TEST_CONNECTION_STRING")))
            Skip = "Use scripts/Test-Interaction.ps1 with a migrated disposable schema.";
    }
}

public sealed class InteractionTests
{
    private readonly TestClock _clock = new();
    private readonly string _user = "fixture:" + Guid.NewGuid().ToString("N");
    private string Connection => Environment.GetEnvironmentVariable("INTERACTION_TEST_CONNECTION_STRING")!;
    private PooledDbContextFactory<InteractionDbContext> Factory => new(new DbContextOptionsBuilder<InteractionDbContext>().UseNpgsql(Connection).Options);
    private IOptions<InteractionOptions> Settings => Options.Create(new InteractionOptions { DigestSize = 2 });
    private SubscriptionService Subscriptions => new(new(Factory), _clock, Settings);
    private NotificationService Notifications => new(Factory, new(Factory), _clock, Settings);
    private FeedbackService Feedback => new(new(Factory), _clock, Settings);

    [Theory]
    [InlineData(3, false)] // 08:00 regional time: quiet hours
    [InlineData(4, true)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void RegionalQuietHoursAreRespected(int utcHour, bool expected) =>
        Assert.Equal(expected, DigestSchedule.CanGenerate(new DateTimeOffset(2036, 11, 14, utcHour, 0, 0, TimeSpan.Zero), 9));

    [PostgresFact]
    public async Task ConsentDeduplicationOwnershipAndVersionedChanges()
    {
        var seed = await SeedAsync();
        var request = new SubscriptionRequest(seed.CityId, ConsentConfirmed: true);
        var rejected = await Assert.ThrowsAsync<ServiceException>(() => Subscriptions.SaveAsync(_user, null, request with { ConsentConfirmed = false }, default));
        Assert.Equal("consent_required", rejected.Code);
        var results = await Task.WhenAll(Subscriptions.SaveAsync(_user, null, request, default), Subscriptions.SaveAsync(_user, null, request, default));
        Assert.Equal(results[0].Item.Id, results[1].Item.Id);
        Assert.Single(results, x => x.Created);
        var current = results[0].Item;
        var stranger = await Assert.ThrowsAsync<ServiceException>(() => Subscriptions.SaveAsync("other:" + _user, current.Id,
            request with { ExpectedVersion = current.Version }, default));
        Assert.Equal(404, stranger.Status);
        var changed = await Subscriptions.SaveAsync(_user, current.Id, request with { Category = EventCategory.Concert, ExpectedVersion = current.Version }, default);
        Assert.True(changed.Item.Version > current.Version);
        await Assert.ThrowsAsync<ServiceException>(() => Subscriptions.SaveAsync(_user, current.Id, request with { ExpectedVersion = current.Version }, default));
        await Subscriptions.DeleteAsync("other:" + _user, current.Id, default);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.True(await db.Subscriptions.AnyAsync(x => x.Id == current.Id));
        await Subscriptions.DeleteAsync(_user, null, default);
        Assert.False(await db.Recipients.AnyAsync(x => x.UserId == _user));
        Assert.False(await db.Subscriptions.AnyAsync(x => x.Id == current.Id));
    }

    [PostgresFact]
    public async Task DigestsAreBoundedDeduplicatedAndAtomicallyLeased()
    {
        var seed = await SeedAsync(count: 4);
        await Subscriptions.SaveAsync(_user, null, new(seed.CityId, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Subscriptions.SaveAsync(_user, null, new(seed.CityId, EventCategory.Concert, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Task.WhenAll(Notifications.GenerateAsync(default), Notifications.GenerateAsync(default));
        var claims = await Task.WhenAll(Notifications.ClaimAsync(20, default), Notifications.ClaimAsync(20, default));
        var own = claims.SelectMany(x => x).Where(x => x.UserId == _user).ToArray();
        var notice = Assert.Single(own);
        Assert.Equal(2, notice.Events.Count);
        await Notifications.AcknowledgeAsync(notice.NotificationId, new(notice.LeaseToken, true), default);
        await Notifications.AcknowledgeAsync(notice.NotificationId, new(notice.LeaseToken, true), default);
        await Notifications.GenerateAsync(default);
        await using var db = await Factory.CreateDbContextAsync();
        var recipient = await db.Recipients.SingleAsync(x => x.UserId == _user);
        Assert.Equal(1, await db.Notifications.CountAsync(x => x.RecipientId == recipient.Id));
        // New day and a fresh catalog check allow the next batch, without replaying prior events.
        _clock.Now = _clock.Now.AddDays(1);
        await RefreshAsync(seed.EventIds);
        await Notifications.GenerateAsync(default);
        var next = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        Assert.DoesNotContain(next.Events, item => notice.Events.Any(previous => previous.Id == item.Id));
    }

    [PostgresFact]
    public async Task CancellationsAndStaleCardsAreNotDelivered()
    {
        var seed = await SeedAsync();
        await Subscriptions.SaveAsync(_user, null, new(seed.CityId, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Notifications.GenerateAsync(default);
        await using var catalog = Catalog();
        var item = await catalog.Events.SingleAsync(x => x.Id == seed.EventIds[0]);
        item.Status = ParserModels.PublicationStatus.Cancelled; item.SourceStatus = ParserModels.SourceEventStatus.Cancelled;
        await catalog.SaveChangesAsync();
        Assert.DoesNotContain(await Notifications.ClaimAsync(20, default), x => x.UserId == _user);
        await using var db = await Factory.CreateDbContextAsync();
        var recipient = await db.Recipients.SingleAsync(x => x.UserId == _user);
        Assert.Equal(NotificationStatus.Suppressed, (await db.Notifications.SingleAsync(x => x.RecipientId == recipient.Id)).Status);
        Assert.Empty(await Notifications.CardsAsync(db, seed.EventIds, default));
    }

    [PostgresFact]
    public async Task ExpiredLeasesCannotAcknowledgeAndFailedDeliveryBacksOff()
    {
        var seed = await SeedAsync();
        await Subscriptions.SaveAsync(_user, null, new(seed.CityId, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Notifications.GenerateAsync(default);
        var first = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        _clock.Now = _clock.Now.AddMinutes(3);
        var second = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        await Assert.ThrowsAsync<ServiceException>(() => Notifications.AcknowledgeAsync(first.NotificationId, new(first.LeaseToken, true), default));
        await Notifications.AcknowledgeAsync(second.NotificationId, new(second.LeaseToken, false, ErrorCode: "transport_unavailable"), default);
        Assert.DoesNotContain(await Notifications.ClaimAsync(20, default), x => x.UserId == _user);
        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Contains(await Notifications.ClaimAsync(20, default), x => x.UserId == _user);
        await Subscriptions.DeleteAsync(_user, null, default);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.False(await db.Notifications.AnyAsync(x => x.Id == second.NotificationId));
    }

    [PostgresFact]
    public async Task DeliveryAlsoRespectsQuietHoursAndLeaseEndsBeforeNight()
    {
        var seed = await SeedAsync();
        await Subscriptions.SaveAsync(_user, null, new(seed.CityId, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Notifications.GenerateAsync(default);
        _clock.Now = new(2036, 11, 14, 15, 59, 55, TimeSpan.Zero);
        var notice = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        Assert.Equal(new DateTimeOffset(2036, 11, 14, 16, 0, 0, TimeSpan.Zero), notice.LeaseExpiresAt);
        _clock.Now = _clock.Now.AddMinutes(1);
        Assert.Empty(await Notifications.ClaimAsync(20, default));
        _clock.Now = new(2036, 11, 15, 4, 0, 0, TimeSpan.Zero);
        var next = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        Assert.NotEqual(notice.LeaseToken, next.LeaseToken);
    }

    [PostgresFact]
    public async Task DraftStalePostponedExpiredAndWrongCategoryCardsNeverEnterDigest()
    {
        var seed = await SeedAsync(count: 6);
        await using var catalog = Catalog();
        var rows = await catalog.Events.Where(x => seed.EventIds.Contains(x.Id)).ToListAsync();
        rows.Single(x => x.Id == seed.EventIds[1]).Status = ParserModels.PublicationStatus.Draft;
        rows.Single(x => x.Id == seed.EventIds[2]).SourceStatus = ParserModels.SourceEventStatus.Postponed;
        rows.Single(x => x.Id == seed.EventIds[3]).VerifiedAt = _clock.Now.AddHours(-25);
        var expired = rows.Single(x => x.Id == seed.EventIds[4]); expired.StartDate = DigestSchedule.LocalDate(_clock.Now).AddDays(-1); expired.DateStart = _clock.Now.AddDays(-1);
        rows.Single(x => x.Id == seed.EventIds[5]).Category = ParserModels.EventCategory.Cinema;
        await catalog.SaveChangesAsync();
        var sub = await Subscriptions.SaveAsync(_user, null, new(seed.CityId, EventCategory.Concert, ConsentConfirmed: true), default);
        await Notifications.GenerateAsync(default);
        await using var db = await Factory.CreateDbContextAsync();
        var notice = await db.Notifications.Include(x => x.Events).SingleAsync(x => x.RecipientId == sub.Item.RecipientId);
        Assert.Equal(seed.EventIds[0], Assert.Single(notice.Events).EventId);
    }

    [PostgresFact]
    public async Task FeedbackRetriesSpamLimitAndModerationArePersistent()
    {
        var seed = await SeedAsync(); var key = Guid.NewGuid();
        var request = new FeedbackRequest(FeedbackKind.Correction, "Уточнение", "Проверьте время начала", seed.EventIds[0]);
        var saved = await Feedback.CreateAsync(_user, key, request, default);
        var repeated = await Feedback.CreateAsync(_user, key, request, default);
        Assert.Equal(saved.Item.Id, repeated.Item.Id); Assert.False(repeated.Created);
        await Assert.ThrowsAsync<ServiceException>(() => Feedback.CreateAsync(_user, key, request with { Message = "Другой текст" }, default));
        await Assert.ThrowsAsync<ServiceException>(() => Feedback.CreateAsync(_user, Guid.NewGuid(), request with { Website = "spam" }, default));
        await Feedback.ReviewAsync(saved.Item.Id, new(saved.Item.Version, FeedbackStatus.InProgress, "Принято в обработку"), default);
        await Assert.ThrowsAsync<ServiceException>(() => Feedback.ReviewAsync(saved.Item.Id, new(saved.Item.Version, FeedbackStatus.Resolved, "Устаревшая версия"), default));
        for (var index = 1; index < 5; index++) await Feedback.CreateAsync(_user, Guid.NewGuid(), request, default);
        var limited = await Assert.ThrowsAsync<ServiceException>(() => Feedback.CreateAsync(_user, Guid.NewGuid(), request, default));
        Assert.Equal(429, limited.Status);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Single(await db.FeedbackReviews.Where(x => x.FeedbackId == saved.Item.Id).ToListAsync());
    }

    [PostgresFact]
    public async Task LateAcknowledgmentDoesNotTriggerCatchUpSpam()
    {
        var seed = await SeedAsync(count: 4);
        var sub = await Subscriptions.SaveAsync(_user, null, new(seed.CityId, Channel: NotificationChannel.Telegram, ConsentConfirmed: true), default);
        await Notifications.GenerateAsync(default);
        _clock.Now = _clock.Now.AddDays(1);
        await RefreshAsync(seed.EventIds);
        var notice = (await Notifications.ClaimAsync(20, default)).Single(x => x.UserId == _user);
        await Notifications.AcknowledgeAsync(notice.NotificationId, new(notice.LeaseToken, true), default);
        await Notifications.GenerateAsync(default);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Notifications.CountAsync(x => x.RecipientId == sub.Item.RecipientId));
    }

    [PostgresFact]
    public async Task CatalogProjectionCannotBeWrittenByInteractionContext()
    {
        var seed = await SeedAsync();
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.CatalogEvents.SingleAsync(x => x.Id == seed.EventIds[0]);
        item.Title = "Accidental edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var source = Catalog();
        Assert.NotEqual("Accidental edit", (await source.Events.SingleAsync(x => x.Id == item.Id)).Title);
    }

    [PostgresFact]
    public async Task CleanupDeletesExpiredDataButRetainsDeduplicationForActiveSubscriptions()
    {
        var seed = await SeedAsync();
        var sub = await Subscriptions.SaveAsync(_user, null, new(seed.CityId, ConsentConfirmed: true), default);
        await Feedback.CreateAsync(_user, Guid.NewGuid(), new(FeedbackKind.Question, "Вопрос", "Тестовое обращение"), default);
        await Notifications.GenerateAsync(default);
        _clock.Now = _clock.Now.AddDays(31);
        await Notifications.CleanupAsync(default);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.False(await db.Notifications.AnyAsync(x => x.RecipientId == sub.Item.RecipientId));
        Assert.True(await db.NotificationEvents.AnyAsync(x => x.SubscriptionId == sub.Item.Id && x.NotificationId == null));
        _clock.Now = _clock.Now.AddDays(61);
        await Notifications.CleanupAsync(default);
        Assert.False(await db.Feedback.AnyAsync(x => x.UserId == _user));
        await Subscriptions.DeleteAsync(_user, null, default);
        Assert.False(await db.NotificationEvents.AnyAsync(x => x.SubscriptionId == sub.Item.Id));
    }

    [PostgresFact]
    public async Task HttpApiEnforcesScopesOwnershipAndErrorContract()
    {
        var seed = await SeedAsync(realTime: true);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../interactionService")),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(SubscriptionService).Assembly.Location);
        foreach (var arg in new[] { "--urls=http://127.0.0.1:0", "--Interaction:WorkerEnabled=false", "--Logging:LogLevel:Default=Warning",
            "--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information", "--Logging:EventLog:LogLevel:Default=None" }) start.ArgumentList.Add(arg);
        var gatewayKey = "gateway-" + Guid.NewGuid().ToString("N"); var adminKey = "admin-" + Guid.NewGuid().ToString("N");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__InteractionDatabase"] = Connection;
        start.Environment["Interaction__ApiKey"] = gatewayKey; start.Environment["Interaction__AdminApiKey"] = adminKey;
        var deliveryKey = "delivery-" + Guid.NewGuid().ToString("N");
        start.Environment["Interaction__DeliveryApiKey"] = deliveryKey;
        var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { var match = Regex.Match(e.Data ?? "", @"Now listening on: (http://127\.0\.0\.1:\d+)"); if (match.Success) address.TrySetResult(new Uri(match.Groups[1].Value)); };
        process.ErrorDataReceived += (_, _) => { };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var client = new HttpClient { BaseAddress = await address.Task.WaitAsync(TimeSpan.FromSeconds(30)) };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            const string prefix = "/api/v1/interaction/me";
            await Error(await client.GetAsync(prefix + "/subscriptions"), 401);
            client.DefaultRequestHeaders.Add("X-Interaction-Key", gatewayKey);
            await Error(await client.GetAsync(prefix + "/subscriptions"), 400);
            client.DefaultRequestHeaders.Add("X-User-Id", _user);
            await Error(await client.PostAsJsonAsync(prefix + "/subscriptions", new { cityId = seed.CityId }), 400);
            using var created = await client.PostAsJsonAsync(prefix + "/subscriptions", new { cityId = seed.CityId, consentConfirmed = true });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var sub = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = sub.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(prefix + "/subscriptions/" + id)).StatusCode);
            await Error(await client.GetAsync("/api/v1/interaction/admin/feedback"), 401);
            client.DefaultRequestHeaders.Add("X-Interaction-Admin-Key", adminKey);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/interaction/admin/feedback")).StatusCode);
            await Error(await client.GetAsync(prefix + "/subscriptions?page=invalid"), 400);
            await Error(await client.PostAsync(prefix + "/feedback", new StringContent("{", Encoding.UTF8, "application/json")), 400);
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var feedbackBody = new { kind = "question", subject = "Вопрос", message = "Уточните сведения", eventId = seed.EventIds[0] };
            using var feedbackCreated = await client.PostAsJsonAsync(prefix + "/feedback", feedbackBody);
            Assert.Equal(HttpStatusCode.Created, feedbackCreated.StatusCode);
            var feedback = await feedbackCreated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(feedback.TryGetProperty("userId", out _)); Assert.False(feedback.TryGetProperty("contentHash", out _));
            var feedbackId = feedback.GetProperty("id").GetGuid();
            client.DefaultRequestHeaders.Add("X-Actor-Id", "moderator:fixture");
            using var review = await client.PatchAsJsonAsync($"/api/v1/interaction/admin/feedback/{feedbackId}/review",
                new { expectedVersion = feedback.GetProperty("version").GetInt64(), status = "inProgress", note = "Принято в работу" });
            Assert.Equal(HttpStatusCode.NoContent, review.StatusCode);
            var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/interaction/admin/feedback/{feedbackId}/history");
            Assert.Equal("moderator:fixture", history[0].GetProperty("actor").GetString());
            using var feedbackRepeat = await client.PostAsJsonAsync(prefix + "/feedback", feedbackBody);
            Assert.Equal(HttpStatusCode.OK, feedbackRepeat.StatusCode);
            Assert.Equal("inProgress", (await feedbackRepeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
            await Error(await client.PostAsJsonAsync(prefix + "/feedback", new { kind = "question", subject = "Изменено", message = "Другой текст" }), 409);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/interaction/admin/notifications/generate", null)).StatusCode);
            var inbox = await client.GetFromJsonAsync<JsonElement>(prefix + "/notifications");
            Assert.True(inbox.TryGetProperty("items", out _));
            await Error(await client.PostAsync("/api/v1/interaction/deliveries/claim", null), 401);
            client.DefaultRequestHeaders.Add("X-Interaction-Delivery-Key", deliveryKey);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/interaction/deliveries/claim", null)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-User-Id"); client.DefaultRequestHeaders.Add("X-User-Id", "stranger:" + _user);
            await Error(await client.GetAsync(prefix + "/subscriptions/" + id), 404);
            await Error(await client.GetAsync(prefix + "/feedback/" + feedbackId), 404);
            client.DefaultRequestHeaders.Remove("X-User-Id"); client.DefaultRequestHeaders.Add("X-User-Id", _user);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(prefix + "/subscriptions")).StatusCode);
            var schema = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
            Assert.StartsWith("3.1", schema.GetProperty("openapi").GetString());
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static async Task Error(HttpResponseMessage response, int status)
    {
        using (response)
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var field in new[] { "type", "title", "status", "instance", "code", "traceId", "errors" }) Assert.True(body.TryGetProperty(field, out _));
        }
    }

    private ParserDb Catalog() => new(new DbContextOptionsBuilder<ParserDb>().UseNpgsql(Connection).Options);
    private async Task<(Guid CityId, Guid[] EventIds)> SeedAsync(int count = 1, bool realTime = false)
    {
        var now = realTime ? DateTimeOffset.UtcNow : _clock.Now;
        await using var db = Catalog();
        var city = new ParserModels.ReferenceCity { Id = Guid.NewGuid(), Name = _user, NormalizedName = _user, CreatedAt = now };
        var venue = new ParserModels.ReferenceVenue { Id = Guid.NewGuid(), CityId = city.Id, Name = "Fixture venue", Address = "Fixture address",
            NormalizedName = "fixture venue", NormalizedAddress = "fixture address", CreatedAt = now };
        await db.Cities.AddAsync(city); await db.Venues.AddAsync(venue);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid(); ids.Add(id);
            var date = DigestSchedule.LocalDate(now).AddDays(3);
            await db.Events.AddAsync(new ParserModels.ParsedEvent { Id = id, DedupKey = SubscriptionService.Hash(id.ToString()), Title = "Fixture " + i,
                NormalizedTitle = "fixture " + i, Category = ParserModels.EventCategory.Concert, CityId = city.Id, VenueId = venue.Id,
                City = city.Name, Venue = venue.Name, Address = venue.Address, StartDate = date, StartTime = new(17, 0),
                DateStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Unspecified), DigestSchedule.Zone)),
                PriceMin = 100, AgeLimit = 6, Status = ParserModels.PublicationStatus.Published, SourceStatus = ParserModels.SourceEventStatus.Scheduled,
                Confidence = .95m, DataHash = SubscriptionService.Hash(id.ToString()), CreatedAt = now, UpdatedAt = now, VerifiedAt = now, Version = 1 });
        }
        await db.SaveChangesAsync(); return (city.Id, ids.ToArray());
    }
    private async Task RefreshAsync(Guid[] ids)
    {
        await using var db = Catalog();
        await db.Events.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifiedAt, _clock.Now));
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2036, 11, 14, 7, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
