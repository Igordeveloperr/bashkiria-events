using Bashkiria.Events.Parser.Models;

namespace Bashkiria.Events.Parser.Contracts;

public sealed record ReviewEventRequest(
    long ExpectedVersion, PublicationStatus Status, string Note,
    DateOnly? StartDate = null, TimeOnly? StartTime = null,
    decimal? PriceMin = null, decimal? PriceMax = null, int? AgeLimit = null,
    SourceEventStatus? ConfirmedSourceStatus = null,
    string? Title = null, string? City = null, string? Venue = null, string? Address = null, string? Description = null);
