using System.ComponentModel.DataAnnotations;

namespace Bashkiria.Events.Parser.Configuration;

public sealed class ParserOptions
{
    public const string SectionName = "Parser";
    [Range(1, 60)] public int RequestTimeoutSeconds { get; set; } = 20;
    [Range(250, 10000)] public int RequestDelayMilliseconds { get; set; } = 500;
    [Range(1, 500)] public int MaxPagesPerRun { get; set; } = 200;
    [Range(1, 30)] public int MaxListingPages { get; set; } = 5;
    [Range(1, 10)] public int MaxConsecutiveFailures { get; set; } = 3;
    [Range(1, 168)] public int FreshnessHours { get; set; } = 24;
    [Range(1, 168)] public int ScheduleIntervalHours { get; set; } = 6;
    public bool SchedulerEnabled { get; set; }
    public string? ApiKey { get; set; }
    public List<SourceOptions> Sources { get; set; } = [];
}

public sealed class SourceOptions
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "Structured";
    public bool Enabled { get; set; } = true;
    public string ListingUrl { get; set; } = string.Empty;
    public string EventPathPattern { get; set; } = "^/events/";
    public int Priority { get; set; } = 50;
    public string? City { get; set; }
    public string? Venue { get; set; }
    public string? Address { get; set; }
    public string? UserAgent { get; set; } = "BashkiriaEventsParser/1.0";
}
