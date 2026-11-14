using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Microsoft.Extensions.Options;

namespace Parser.Tests;

public sealed class NewSourceParserTests
{
    private static readonly TimeProvider Clock = new FixedClock();
    private static ISourceEventParser[] Parsers() =>
        [new GkzSourceParser(new GkzParser()), new StructuredEventParser(), new GorodzovetParser(new StructuredEventParser()), new BashgfParser(), new BashoperaParser(Clock)];

    private static SourceOptions Source(string id) => id switch
    {
        "gorodzovet" => new()
        {
            Id = id, Kind = SourceKinds.Gorodzovet, ListingUrl = "https://gorodzovet.ru/ufa/culture/",
            EventPathPattern = "^/ufa/[^/]+-event[0-9]+/?$", City = "Уфа"
        },
        "bashgf" => new()
        {
            Id = id, Kind = SourceKinds.Bashgf, ListingUrl = "https://bashgf.ru/afisha/",
            EventPathPattern = "^/afisha/event/[^/]+/?$", City = "Уфа", Venue = "Башкирская филармония", Address = "ул. Гоголя, 58", UserAgent = null
        },
        "bashopera" => new()
        {
            Id = id, Kind = SourceKinds.Bashopera, ListingUrl = "https://bashopera.ru/affiche/",
            EventPathPattern = "^/repertoire/[^/]+/[0-9]+/?$", City = "Уфа", Venue = "Башкирский театр оперы и балета", Address = "ул. Ленина, 5/1"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    [Theory]
    [InlineData("gorodzovet")]
    [InlineData("bashgf")]
    [InlineData("bashopera")]
    public void RealListingsDiscoverOnlySameHostCardUrls(string id)
    {
        var source = Source(id);
        var registry = new SourceRegistry(Options.Create(new ParserOptions { Sources = [source] }), Parsers());
        var links = registry.DiscoverLinks(source, Fixture(id + "-listing"), new Uri(source.ListingUrl));
        Assert.NotEmpty(links);
        Assert.Equal(links.Count, links.Distinct().Count());
        Assert.All(links, link => Assert.Equal(new Uri(source.ListingUrl).Host, link.Host));
        if (id == "bashopera") Assert.All(links, link => Assert.Equal(3, link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void GorodzovetSplitsSessionsInsteadOfInventingOneContinuousEvent()
    {
        var source = Source("gorodzovet");
        var url = new Uri("https://gorodzovet.ru/ufa/sobranie-sochinenii-event11461361");
        var items = new GorodzovetParser(new StructuredEventParser()).Parse(Fixture("gorodzovet-card"), url, source);
        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { new DateOnly(2026, 11, 25), new DateOnly(2027, 1, 7) }, items.Select(item => item.StartDate!.Value));
        Assert.All(items, item =>
        {
            Assert.Equal("Собрание сочинений", item.Title);
            Assert.Equal(new TimeOnly(19, 0), item.StartTime);
            Assert.Equal(500, item.PriceMin);
            Assert.Equal(800, item.PriceMax);
            Assert.Contains(item.Issues, issue => issue.Code == "session_identity_unconfirmed");
            Assert.True(item.RequiresReview);
        });
        Assert.Null(items[0].EndDate);
        Assert.Equal(new DateOnly(2027, 1, 7), items[1].EndDate);
        Assert.Equal(new TimeOnly(22, 0), items[1].EndTime);
        Assert.NotEqual(items[0].ExternalId, items[1].ExternalId);
    }

    [Fact]
    public void GorodzovetDoesNotAcceptAnExplicitForeignRegion()
    {
        var html = Fixture("gorodzovet-card").Replace("\"addressRegion\": \"Уфа\"", "\"addressRegion\": \"Татарстан\"");
        Assert.Contains("Татарстан", html);
        Assert.Empty(new GorodzovetParser(new StructuredEventParser()).Parse(html,
            new Uri("https://gorodzovet.ru/ufa/sobranie-sochinenii-event11461361"), Source("gorodzovet")));
    }

    [Fact]
    public void MissingVisibleScheduleRequiresReview()
    {
        const string html = """
            <h1>Fixture</h1><script type="application/ld+json">{"@type":"Event","name":"Fixture",
            "startDate":"2036-11-15T17:00:00+05:00","location":{"name":"Fixture venue",
            "address":{"addressLocality":"Уфа","addressRegion":"Уфа"}}}</script>
            """;
        var item = Assert.Single(new GorodzovetParser(new StructuredEventParser()).Parse(html,
            new Uri("https://gorodzovet.ru/ufa/fixture-event123"), Source("gorodzovet")));
        Assert.Contains(item.Issues, issue => issue.Code == "schedule_unconfirmed");
    }

    [Fact]
    public void PhilharmonicReadsRealPriceAndDoesNotInventZeroDurationOrAge()
    {
        var item = Assert.Single(new BashgfParser().Parse(Fixture("bashgf-card"),
            new Uri("https://bashgf.ru/afisha/event/jubilejnyj-koncert-azamata-hasanshina/"), Source("bashgf")));
        Assert.Equal("Освобождение звука", item.Title);
        Assert.Equal(new DateOnly(2026, 11, 14), item.StartDate);
        Assert.Equal(new TimeOnly(18, 0), item.StartTime);
        Assert.Equal(600, item.PriceMin);
        Assert.Equal(800, item.PriceMax);
        Assert.Null(item.EndDate);
        Assert.Null(item.EndTime);
        Assert.Null(item.AgeLimit);
        Assert.Equal(SourceEventStatus.Unknown, item.SourceStatus);
        Assert.Equal("post-15730", item.ExternalId);
        Assert.NotNull(item.OrgLink);
    }

    [Fact]
    public void PhilharmonicFollowsTheRealPaginationLinkAndRejectsForeignNextHost()
    {
        var source = Source("bashgf");
        var registry = new SourceRegistry(Options.Create(new ParserOptions { Sources = [source] }), Parsers());
        var next = registry.NextListingPage(source, Fixture("bashgf-listing"), new Uri(source.ListingUrl), 2);
        Assert.Equal("https://bashgf.ru/afisha/page/2/", next?.AbsoluteUri);
        Assert.Null(registry.NextListingPage(source, "<a class='next page-numbers' href='https://example.org/'>next</a>", new Uri(source.ListingUrl), 2));
    }

    [Fact]
    public void OperaRetainsStableSessionIdsAndMarksInferredYearsForReview()
    {
        var items = new BashoperaParser(Clock).Parse(Fixture("bashopera-card"), new Uri("https://bashopera.ru/repertoire/opera/15866/"), Source("bashopera"));
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { new DateOnly(2026, 11, 14), new DateOnly(2027, 1, 16), new DateOnly(2027, 1, 17) }, items.Select(item => item.StartDate!.Value));
        Assert.Equal(3, items.Select(item => item.ExternalId).Distinct().Count());
        Assert.All(items, item =>
        {
            Assert.Equal("Травиата", item.Title);
            Assert.Equal(new TimeOnly(18, 0), item.StartTime);
            Assert.Equal(new TimeOnly(20, 50), item.EndTime);
            Assert.Equal(item.StartDate, item.EndDate);
            Assert.Equal(16, item.AgeLimit);
            Assert.Equal(EventCategory.Theatre, item.Category);
            Assert.Null(item.PriceMin);
            Assert.Contains(item.Issues, issue => issue.Code == "inferred_start_year");
            Assert.DoesNotContain("VK.Widgets", item.Description);
        });
    }

    [Fact]
    public void OperaDoesNotUsePremiereDateAsSessionDate()
    {
        const string html = """
            <h1>Fixture</h1><div class="b-event"><div class="info"><div class="description">Дата премьеры: 13.11.2026</div></div>
            <div class="affiche"><div class="event"><div class="date"><div class="left2">15</div><div class="month">ноября</div>
            <div class="time">18:00</div></div><div class="buy"><a href="123/">Билеты</a></div></div></div></div>
            """;
        var item = Assert.Single(new BashoperaParser(Clock).Parse(html, new Uri("https://bashopera.ru/repertoire/opera/1/"), Source("bashopera")));
        Assert.Null(item.StartDate);
        Assert.True(item.RequiresReview);
    }

    [Fact]
    public void OperaDoesNotCollectPromotionalOrTicketIframeLinksAsEventCards()
    {
        var source = Source("bashopera");
        var registry = new SourceRegistry(Options.Create(new ParserOptions { Sources = [source] }), Parsers());
        const string html = """
            <a href="/repertoire/concert/14117/">Old promotional link</a>
            <div class="item content"><div class="name"><a href="/repertoire/opera/15866/">Current opera</a></div>
            <a href="/repertoire/opera/15866/15868/">Tickets</a></div>
            """;
        var link = Assert.Single(registry.DiscoverLinks(source, html, new Uri(source.ListingUrl)));
        Assert.Equal("/repertoire/opera/15866/", link.AbsolutePath);
    }

    [Fact]
    public void OperaSessionsWithoutTicketIdsDoNotOverwriteOneAnother()
    {
        const string html = """
            <h1>Fixture</h1><div class="b-event"><div class="affiche">
            <div class="event"><div class="date"><div class="left2">14</div><div class="month">ноября 2026</div><div class="time">18:00</div></div></div>
            <div class="event"><div class="date"><div class="left2">15</div><div class="month">ноября 2026</div><div class="time">18:00</div></div></div>
            </div></div>
            """;
        var items = new BashoperaParser(Clock).Parse(html, new Uri("https://bashopera.ru/repertoire/opera/1/"), Source("bashopera"));
        Assert.Equal(2, items.Count);
        Assert.NotEqual(items[0].ExternalId, items[1].ExternalId);
        Assert.All(items, item => Assert.Contains(item.Issues, issue => issue.Code == "session_identity_unconfirmed"));
    }

    [Fact]
    public void GenericPaginationPreservesFiltersAndReplacesPageParameter()
    {
        var url = new StructuredEventParser().NextListingPage("", new Uri("https://example.org/afisha?region=ufa&page=2"), 3);
        Assert.Equal("?region=ufa&page=3", url?.Query);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sources", name + ".html"));
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 14, 7, 0, 0, TimeSpan.Zero);
    }
}
