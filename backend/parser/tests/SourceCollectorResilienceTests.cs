using System.Net;
using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Parser.Tests;

public sealed class SourceCollectorResilienceTests
{
    [Fact]
    public async Task OutageStopsCollectionBeforeEveryCardTimesOut()
    {
        var options = Options.Create(new ParserOptions
        {
            RequestDelayMilliseconds = 0,
            MaxConsecutiveFailures = 3,
            Sources = [new() { Id = "gkz", Kind = "Gkz", ListingUrl = GkzClient.ListingUrl.AbsoluteUri, EventPathPattern = "^/events/" }]
        });
        var handler = new OutageHandler();
        using var http = new HttpClient(handler);
        var registry = new SourceRegistry(options, new GkzParser(), new StructuredEventParser());
        var collector = new SourceCollector(registry, new SourcePageClient(http, new Documents()), new Events(),
            new CandidateNormalizer(TimeProvider.System), options, NullLogger<SourceCollector>.Instance);

        var result = await collector.CollectAsync("gkz", 5, null, CancellationToken.None);

        Assert.Equal(5, result.DiscoveredPages);
        Assert.Equal(3, result.ProcessedPages);
        Assert.Equal(4, handler.Requests);
        Assert.Contains(result.Errors, error => error.Code == "source_unavailable");
        Assert.Empty(result.Items);
    }

    private sealed class OutageHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/afisha/"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Join("", Enumerable.Range(1, 5).Select(id => $"<a href='/events/fixture-{id}/'>Card</a>"))) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class Documents : IRawDocumentStore
    {
        public Task SaveAsync(RawDocument document, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Events : IEventStore
    {
        public Task<EventSaveResult> SaveAsync(SourceOptions source, EventCandidate candidate, RawDocument document, CancellationToken token) =>
            throw new InvalidOperationException("Unavailable cards must not produce events.");
    }
}
