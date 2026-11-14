namespace Bashkiria.Events.Interaction.Models;

// Read-only projections of the shared catalog contract. This service never writes catalog tables.
public sealed class CatalogCity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CatalogEvent
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public Guid? CityId { get; set; }
    public Guid? VenueId { get; set; }
    public string City { get; set; } = "";
    public string Venue { get; set; } = "";
    public string Address { get; set; } = "";
    public EventCategory Category { get; set; }
    public string Status { get; set; } = "";
    public string SourceStatus { get; set; } = "";
    public DateOnly? StartDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTimeOffset? DateStart { get; set; }
    public DateTimeOffset? DateEnd { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }
    public int? AgeLimit { get; set; }
}
