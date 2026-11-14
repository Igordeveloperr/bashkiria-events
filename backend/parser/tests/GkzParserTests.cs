using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;

namespace Parser.Tests;

public sealed class GkzParserTests
{
    [Theory]
    [InlineData("konczert-narodnogo-artista-rossii-aleksandra-malinina", 13, 19)]
    [InlineData("spektakl-pyatniczy-abelya-po-pese-osvalda-zagradnika-solo-dlya-chasov-s-boem-550-613", 14, 18)]
    [InlineData("yubilejnyj-gala-konczert-studii-diva", 15, 17)]
    public void RealCardsUseVisibleTimeAndReportCalendarConflict(string slug, int day, int hour)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", slug + ".html"));
        var item = Parse(html);
        Assert.Equal(new DateOnly(2026, 11, day), item.StartDate);
        Assert.Equal(new TimeOnly(hour, 0), item.StartTime);
        Assert.True(item.RequiresReview);
        Assert.Contains(item.Issues, issue => issue.Code == "start_time_conflict");
    }

    [Fact]
    public void ConsistentScheduleDoesNotRequireReview()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <div class="mec-single-event-date"><span class="mec-start-date-label">Ноя 15 2026</span></div>
            <div class="mec-single-event-time"><dd><abbr>17:00</abbr></dd></div>
            <script type="application/ld+json">{"@type":"Event","startDate":"2026-11-15"}</script>
            <a href="https://calendar.google.com/calendar/render?dates=20261115T120000Z/20261115T140000Z">Calendar</a>
            """);
        Assert.Equal(new TimeOnly(17, 0), item.StartTime);
        Assert.Empty(item.Issues);
        Assert.False(item.RequiresReview);
    }

    [Fact]
    public void DateOnlyJsonLdDoesNotInventMidnight()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <script type="application/ld+json">{"@graph":[{"@type":"Event","startDate":"2026-11-15"}]}</script>
            """);
        Assert.Equal(new DateOnly(2026, 11, 15), item.StartDate);
        Assert.Null(item.StartTime);
        Assert.True(item.RequiresReview);
    }

    [Fact]
    public void CalendarUtcFallbackHandlesNextLocalDay()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <a href="https://calendar.google.com/calendar/render?dates=20261115T220000Z/20261116T000000Z">Calendar</a>
            """);
        Assert.Equal(new DateOnly(2026, 11, 16), item.StartDate);
        Assert.Equal(new TimeOnly(3, 0), item.StartTime);
        Assert.True(item.RequiresReview);
        Assert.Contains(item.Issues, issue => issue.Code == "calendar_time_unconfirmed");
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/render")]
    [InlineData("https://calendar.google.com/calendar/render?dates=20260230T170000/20260230T190000&amp;ctz=Asia%2FYekaterinburg")]
    [InlineData("https://calendar.google.com/calendar/render?dates=20261115T170000/20261115T190000")]
    [InlineData("https://calendar.google.com/calendar/render?dates=20261115T170000/20261115T190000&amp;ctz=Europe%2FMoscow")]
    public void MissingOrInvalidCalendarDataRemainsUnknown(string url)
    {
        var item = Parse($"<h1>Fixture</h1><a href=\"{url}\">Calendar</a>");
        Assert.Null(item.StartDate);
        Assert.Null(item.StartTime);
        Assert.True(item.RequiresReview);
    }

    [Fact]
    public void InvalidVisibleDateRequiresReview()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <div class="mec-single-event-date"><span class="mec-start-date-label">Фев 30 2026</span></div>
            <div class="mec-single-event-time"><dd><abbr>17:00</abbr></dd></div>
            """);
        Assert.Null(item.StartDate);
        Assert.Contains(item.Issues, issue => issue.Code == "invalid_visible_date");
        Assert.True(item.RequiresReview);
    }

    [Fact]
    public void ConflictingStructuredDateRequiresReview()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <div class="mec-single-event-date"><span class="mec-start-date-label">Ноя 15 2026</span></div>
            <div class="mec-single-event-time"><dd><abbr>17:00</abbr></dd></div>
            <script type="application/ld+json">{"@type":"Event","startDate":"2026-11-16"}</script>
            """);
        Assert.Equal(new DateOnly(2026, 11, 15), item.StartDate);
        Assert.Contains(item.Issues, issue => issue.Code == "start_date_conflict");
        Assert.True(item.RequiresReview);
    }

    [Fact]
    public void CalendarTimeFromDifferentDayIsNotCombinedWithVisibleDate()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <div class="mec-single-event-date"><span class="mec-start-date-label">Ноя 15 2026</span></div>
            <a href="https://calendar.google.com/calendar/render?dates=20261116T120000Z/20261116T140000Z">Calendar</a>
            """);
        Assert.Equal(new DateOnly(2026, 11, 15), item.StartDate);
        Assert.Null(item.StartTime);
        Assert.Contains(item.Issues, issue => issue.Code == "start_date_conflict");
    }

    [Fact]
    public void MalformedJsonLdDoesNotHideVisibleSchedule()
    {
        var item = Parse("""
            <h1>Fixture</h1>
            <div class="mec-single-event-date"><span class="mec-start-date-label">Ноя 15 2026</span></div>
            <div class="mec-single-event-time"><dd><abbr>17:00</abbr></dd></div>
            <script type="application/ld+json">{broken</script>
            """);
        Assert.Equal(new DateOnly(2026, 11, 15), item.StartDate);
        Assert.Equal(new TimeOnly(17, 0), item.StartTime);
    }

    private static EventCandidate Parse(string html) => Assert.Single(
        new GkzParser().Parse(html, new Uri("https://gkzbashkortostan.ru/events/test-fixture/")));
}
