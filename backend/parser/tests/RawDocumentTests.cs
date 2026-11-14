using Bashkiria.Events.Parser.Models;
using System.Net;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Processing;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Bashkiria.Events.Parser.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Parser.Tests;

public sealed class RawDocumentTests
{
    private static readonly Uri PageUrl = new("https://gkzbashkortostan.ru/events/fixture/");

    [Fact]
    public async Task DownloadPreservesBodyStatusTimestampAndHash()
    {
        const string content = "<h1>Тестовая карточка</h1>";
        var store = new MemoryStore();
        var handler = new FixtureHandler(_ => Html(content));
        using var http = new HttpClient(handler);
        var before = DateTimeOffset.UtcNow;

        var document = await new GkzClient(http, store).GetEventPageAsync(PageUrl, CancellationToken.None);

        Assert.Same(document, Assert.Single(store.Documents));
        Assert.NotEqual(Guid.Empty, document.Id);
        Assert.Equal("gkz", document.SourceId);
        Assert.Equal(PageUrl, document.Url);
        Assert.Equal(200, document.HttpStatus);
        Assert.Equal(content, document.Content);
        Assert.InRange(document.FetchedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), document.ContentHash);
    }

    [Fact]
    public async Task UnsuccessfulResponseIsSavedBeforeThrowingHttpError()
    {
        var store = new MemoryStore();
        using var http = new HttpClient(new FixtureHandler(_ =>
            new(HttpStatusCode.NotFound) { Content = new StringContent("Missing card") }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GkzClient(http, store).GetEventPageAsync(PageUrl, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        var document = Assert.Single(store.Documents);
        Assert.Equal(404, document.HttpStatus);
        Assert.Equal("Missing card", document.Content);
    }

    [Fact]
    public async Task ParseFailureKeepsBothListingAndCard()
    {
        var store = new MemoryStore();
        using var http = new HttpClient(new FixtureHandler(url => url.AbsolutePath == "/afisha/"
            ? Html("<a href='/events/fixture/'>Card</a>") : Html("<h1>Fixture</h1>")));
        var parser = new CallbackParser(html =>
        {
            Assert.Equal(2, store.Documents.Count);
            Assert.Equal(html, store.Documents[1].Content);
            throw new FormatException("Simulated markup failure");
        });
        var collector = CreateCollector(http, store, parser);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(2, store.Documents.Count);
        Assert.Equal("parse_failed", Assert.Single(result.Errors).Code);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task StorageFailurePreventsParsingAndStopsCollection()
    {
        var store = new MemoryStore { FailOnSave = 2 };
        using var http = new HttpClient(new FixtureHandler(url => url.AbsolutePath == "/afisha/"
            ? Html("<a href='/events/fixture/'>Card</a>") : Html("<h1>Fixture</h1>")));
        var parsed = false;
        var parser = new CallbackParser(_ => { parsed = true; return []; });
        var collector = CreateCollector(http, store, parser);

        await Assert.ThrowsAsync<ParserStorageException>(() => collector.CollectAsync(CancellationToken.None));

        Assert.False(parsed);
        Assert.Single(store.Documents);
    }

    [Fact]
    public async Task CollectorReturnsDocumentIdsForProvenance()
    {
        var store = new MemoryStore();
        using var http = new HttpClient(new FixtureHandler(url => url.AbsolutePath == "/afisha/"
            ? Html("<a href='/events/fixture/'>Card</a>") : Html("<h1>Fixture</h1>")));
        var collector = CreateCollector(http, store, new GkzParser());

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(store.Documents[0].Id, result.ListingDocumentId);
        Assert.Equal(store.Documents[1].Id, Assert.Single(result.Items).RawDocumentId);
    }

    [Fact]
    public async Task ForeignUrlDoesNotReachHttpOrStorage()
    {
        var store = new MemoryStore();
        var handler = new FixtureHandler(_ => throw new Exception("Unexpected network request"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new GkzClient(http, store).GetEventPageAsync(new Uri("https://example.org/events/fixture/"), CancellationToken.None));

        Assert.Empty(store.Documents);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SameSourceRedirectIsFollowedAndBothResponsesArePreserved()
    {
        var store = new MemoryStore();
        var handler = new FixtureHandler(url =>
        {
            if (url.AbsolutePath == "/events/fixture/")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri("/events/renamed/", UriKind.Relative);
                return redirect;
            }
            return Html("<h1>Redirected fixture</h1>");
        });
        using var http = new HttpClient(handler);
        var result = await new GkzClient(http, store).GetEventPageAsync(PageUrl, CancellationToken.None);
        Assert.Equal(2, store.Documents.Count);
        Assert.Equal(301, store.Documents[0].HttpStatus);
        Assert.Equal("/events/renamed/", result.Url.AbsolutePath);
    }

    [Fact]
    public async Task CrossHostRedirectIsRejectedBeforeFollowingIt()
    {
        var store = new MemoryStore();
        var handler = new FixtureHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://example.org/events/fixture/");
            return redirect;
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new GkzClient(http, store).GetEventPageAsync(PageUrl, CancellationToken.None));
        Assert.Single(store.Documents);
        Assert.Equal(1, handler.RequestCount);
    }

    private static GkzCollector CreateCollector(HttpClient http, IRawDocumentStore store, IEventParser parser)
    {
        var options = Options.Create(new ParserOptions
        {
            RequestDelayMilliseconds = 0,
            Sources = [new() { Id = "gkz", Kind = "Gkz", ListingUrl = GkzClient.ListingUrl.AbsoluteUri, EventPathPattern = "^/events/[^/]+/?$" }]
        });
        var registry = new SourceRegistry(options, [new GkzSourceParser(parser), new StructuredEventParser()]);
        return new GkzCollector(new SourceCollector(registry, new SourcePageClient(http, store), new MemoryEventStore(),
            new CandidateNormalizer(TimeProvider.System), options, NullLogger<SourceCollector>.Instance));
    }

    private sealed class MemoryEventStore : IEventStore
    {
        public Task<EventSaveResult> SaveAsync(SourceOptions source, EventCandidate candidate, RawDocument document, CancellationToken token) =>
            Task.FromResult(new EventSaveResult(Guid.NewGuid(), "added", PublicationStatus.Draft));
    }

    private static HttpResponseMessage Html(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/html") };

    private sealed class MemoryStore : IRawDocumentStore
    {
        public List<RawDocument> Documents { get; } = [];
        public int? FailOnSave { get; init; }

        public Task SaveAsync(RawDocument document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Documents.Count + 1 == FailOnSave)
                throw new ParserStorageException("Simulated database failure");
            Documents.Add(document);
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackParser(Func<string, IReadOnlyList<EventCandidate>> parse) : IEventParser
    {
        public string SourceId => "gkz";
        public IReadOnlyList<EventCandidate> Parse(string html, Uri pageUrl) => parse(html);
    }

    private sealed class FixtureHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
