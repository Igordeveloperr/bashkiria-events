namespace Bashkiria.Events.Parser.Contracts;

public sealed record CreateCityRequest(string Name);
public sealed record CreateVenueRequest(Guid CityId, string Name, string Address);
public sealed record CreateCityAliasRequest(string Name);
public sealed record CreateVenueAliasRequest(string Name, string? Address = null);
public sealed record ReferenceAliasResponse(Guid Id, string Name, string? Address = null);
public sealed record CityResponse(Guid Id, string Name, IReadOnlyList<ReferenceAliasResponse> Aliases);
public sealed record VenueResponse(Guid Id, Guid CityId, string Name, string Address, IReadOnlyList<ReferenceAliasResponse> Aliases);
public sealed record ReferenceNormalizationResult(Guid EventId, string Outcome, IReadOnlyList<string> Issues);
