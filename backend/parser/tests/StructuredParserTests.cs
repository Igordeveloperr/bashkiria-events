using System.Text.Json.Nodes;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;

namespace Parser.Tests;

public sealed class StructuredParserTests
{
    private static readonly SourceOptions Source = new()
    {
        Id = "culture",
        ListingUrl = "https://www.culture.ru/afisha/respublika-bashkortostan",
        EventPathPattern = "^/events/[0-9]+/"
    };
    private static readonly Uri Page = new("https://www.culture.ru/events/123/fixture/");

    [Fact]
    public void JsonLdReferencesRetainRegionalPlaceAndOffer()
    {
        var html = """
            <script type="application/ld+json">
            {"@graph":[
              {"@type":"MusicEvent","name":"Fixture concert","url":"/events/123/fixture/",
               "startDate":"2036-11-15T12:00:00Z","eventStatus":"https://schema.org/EventScheduled",
               "location":{"@id":"place"},"offers":{"@id":"offer"},"contentRating":"6+"},
              {"@id":"place","@type":"Place","name":"Fixture venue","address":{
                "addressRegion":"Республика Башкортостан","addressLocality":"Уфа","streetAddress":"Fixture address"}},
              {"@id":"offer","@type":"AggregateOffer","lowPrice":"300","highPrice":"800","priceCurrency":"RUB"}
            ]}
            </script>
            """;
        var item = Assert.Single(new StructuredEventParser().Parse(html, Page, Source));
        Assert.Equal("Уфа", item.City);
        Assert.Equal(new TimeOnly(17, 0), item.StartTime);
        Assert.Equal(300, item.PriceMin);
        Assert.Equal(800, item.PriceMax);
        Assert.Equal(6, item.AgeLimit);
        Assert.Equal(EventCategory.Concert, item.Category);
        Assert.Equal(SourceEventStatus.Scheduled, item.SourceStatus);
    }

    [Fact]
    public void MicrodataPreservesNestedPlaceAndUnknownPrice()
    {
        var html = """
            <article itemscope itemtype="https://schema.org/Event">
              <h1 itemprop="name">Fixture exhibition</h1>
              <meta itemprop="startDate" content="2036-11-15">
              <div itemprop="location" itemscope itemtype="https://schema.org/Place">
                <span itemprop="name">Fixture venue</span>
                <div itemprop="address" itemscope itemtype="https://schema.org/PostalAddress">
                  <meta itemprop="addressRegion" content="Республика Башкортостан">
                  <meta itemprop="addressLocality" content="Уфа">
                </div>
              </div>
            </article>
            """;
        var item = Assert.Single(new StructuredEventParser().Parse(html, Page, Source));
        Assert.Equal("Fixture exhibition", item.Title);
        Assert.Equal("Fixture venue", item.Venue);
        Assert.Null(item.StartTime);
        Assert.Null(item.PriceMin);
        Assert.Null(item.IsFree);
    }

    [Theory]
    [InlineData("Москва")]
    [InlineData("Республика Татарстан")]
    public void ExplicitOtherRegionIsExcluded(string region)
    {
        var json = new JsonObject
        {
            ["@type"] = "Event",
            ["name"] = "Fixture",
            ["startDate"] = "2036-11-15",
            ["location"] = new JsonObject { ["name"] = "Fixture venue", ["address"] = new JsonObject { ["addressRegion"] = region, ["addressLocality"] = "Уфа" } }
        };
        Assert.Empty(new StructuredEventParser().Parse($"<script type='application/ld+json'>{json}</script>", Page, Source));
    }

    [Fact]
    public void ExplicitFreeAndCancellationArePreserved()
    {
        var html = """
            <script type="application/ld+json">{"@type":"Event","name":"Fixture","startDate":"2036-11-15T17:00:00+05:00",
              "eventStatus":"https://schema.org/EventCancelled","isAccessibleForFree":true,
              "location":{"name":"Fixture venue","address":{"addressRegion":"Башкортостан","addressLocality":"Уфа"}}}
            </script>
            """;
        var item = Assert.Single(new StructuredEventParser().Parse(html, Page, Source));
        Assert.Equal(0, item.PriceMin);
        Assert.True(item.IsFree);
        Assert.Equal(SourceEventStatus.Cancelled, item.SourceStatus);
    }

    [Fact]
    public void NormalizationDoesNotMergeSessionsOrInventUnknownValues()
    {
        var normalizer = new CandidateNormalizer(TimeProvider.System);
        var original = new EventCandidate
        {
            Title = " Fixture ",
            City = "Уфа",
            Venue = "ГКЗ «Башкортостан»",
            SourceUrl = Page,
            StartDate = new DateOnly(2036, 11, 15),
            StartTime = new TimeOnly(17, 0)
        };
        var first = normalizer.Normalize("culture", original);
        var second = normalizer.Normalize("culture", original with { StartTime = new TimeOnly(19, 0) });
        Assert.NotEqual(first.DedupKey, second.DedupKey);
        Assert.Null(first.Candidate.PriceMin);
        Assert.Null(first.Candidate.AgeLimit);
        Assert.Contains(first.Candidate.Issues, issue => issue.Code == "unknown_price");
        Assert.Contains(first.Candidate.Issues, issue => issue.Code == "unconfirmed_status");
    }

    [Fact]
    public void TextPriceHandlesNonBreakingSpaces()
    {
        var price = EventFields.Price(null, "от 1\u00a0500 до 2\u00a0000 руб");
        Assert.Equal(1500, price.Min);
        Assert.Equal(2000, price.Max);
    }

    [Fact]
    public void SessionsWithExplicitIdsKeepStableSourceIdentities()
    {
        var html = """
            <script type="application/ld+json">[
              {"@type":"Event","@id":"session-one","name":"Fixture","startDate":"2036-11-15T17:00:00+05:00"},
              {"@type":"Event","@id":"session-two","name":"Fixture","startDate":"2036-11-15T19:00:00+05:00"}
            ]</script>
            """;
        var items = new StructuredEventParser().Parse(html, Page, Source);
        Assert.Equal(new[] { "session-one", "session-two" }, items.Select(item => item.ExternalId));
        Assert.All(items, item => Assert.DoesNotContain(item.Issues, issue => issue.Code == "session_identity_unconfirmed"));
    }

    [Fact]
    public void SessionsWithoutIdsAreMarkedForReview()
    {
        var html = """
            <script type="application/ld+json">[
              {"@type":"Event","name":"Fixture","startDate":"2036-11-15T17:00:00+05:00"},
              {"@type":"Event","name":"Fixture","startDate":"2036-11-15T19:00:00+05:00"}
            ]</script>
            """;
        var items = new StructuredEventParser().Parse(html, Page, Source);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Contains(item.Issues, issue => issue.Code == "session_identity_unconfirmed"));
    }
}
