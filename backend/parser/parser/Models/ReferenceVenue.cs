namespace Bashkiria.Events.Parser.Models;

public sealed class ReferenceVenue
{
    public Guid Id { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string NormalizedAddress { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class VenueAlias
{
    public Guid Id { get; set; }
    public Guid VenueId { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    // Empty means a name alias; a nonempty key identifies an explicit address variant.
    public string NormalizedAddress { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
