using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Processing;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Parser.References;

public sealed class ReferenceCatalog(IDbContextFactory<ParserDbContext> contexts, TimeProvider clock)
{
    public async Task<Guid> CreateCityAsync(CreateCityRequest request, CancellationToken token)
    {
        var name = Text(request.Name, 200);
        var key = ReferenceKeys.City(name);
        if (key.Length == 0) throw new ParserValidationException("Укажите название населённого пункта.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var city = new ReferenceCity { Id = Guid.NewGuid(), Name = name, NormalizedName = key, CreatedAt = clock.GetUtcNow() };
        await db.Cities.AddAsync(city, token);
        await db.CityAliases.AddAsync(new CityAlias
        { Id = Guid.NewGuid(), CityId = city.Id, Name = name, NormalizedName = key, CreatedAt = clock.GetUtcNow() }, token);
        await db.SaveChangesAsync(token);
        return city.Id;
    }

    public async Task<Guid> CreateVenueAsync(CreateVenueRequest request, CancellationToken token)
    {
        var name = Text(request.Name, 500);
        var address = Text(request.Address, 2000);
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Cities.AnyAsync(item => item.Id == request.CityId, token)) throw new KeyNotFoundException();
        var venue = new ReferenceVenue
        {
            Id = Guid.NewGuid(), CityId = request.CityId, Name = name, Address = address,
            NormalizedName = ReferenceKeys.Name(name), NormalizedAddress = ReferenceKeys.Address(address), CreatedAt = clock.GetUtcNow()
        };
        await db.Venues.AddAsync(venue, token);
        await db.VenueAliases.AddAsync(new VenueAlias
        {
            Id = Guid.NewGuid(), VenueId = venue.Id, CityId = venue.CityId, Name = name, Address = address,
            NormalizedName = venue.NormalizedName, NormalizedAddress = venue.NormalizedAddress, CreatedAt = clock.GetUtcNow()
        }, token);
        await db.SaveChangesAsync(token);
        return venue.Id;
    }

    public async Task<Guid> AddCityAliasAsync(Guid cityId, CreateCityAliasRequest request, CancellationToken token)
    {
        var name = Text(request.Name, 200);
        var key = ReferenceKeys.City(name);
        if (key.Length == 0) throw new ParserValidationException("Укажите название населённого пункта.");
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Cities.AnyAsync(item => item.Id == cityId, token)) throw new KeyNotFoundException();
        var alias = new CityAlias { Id = Guid.NewGuid(), CityId = cityId, Name = name, NormalizedName = key, CreatedAt = clock.GetUtcNow() };
        await db.CityAliases.AddAsync(alias, token);
        await db.SaveChangesAsync(token);
        return alias.Id;
    }

    public async Task<Guid> AddVenueAliasAsync(Guid venueId, CreateVenueAliasRequest request, CancellationToken token)
    {
        var name = Text(request.Name, 500);
        var address = request.Address is null ? string.Empty : Text(request.Address, 2000);
        await using var db = await contexts.CreateDbContextAsync(token);
        var venue = await db.Venues.SingleOrDefaultAsync(item => item.Id == venueId, token) ?? throw new KeyNotFoundException();
        var alias = new VenueAlias
        {
            Id = Guid.NewGuid(), VenueId = venue.Id, CityId = venue.CityId, Name = name, Address = address,
            NormalizedName = ReferenceKeys.Name(name), NormalizedAddress = ReferenceKeys.Address(address), CreatedAt = clock.GetUtcNow()
        };
        await db.VenueAliases.AddAsync(alias, token);
        await db.SaveChangesAsync(token);
        return alias.Id;
    }

    private static string Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Contains('\0') || ReferenceKeys.Name(value).Length == 0)
            throw new ParserValidationException($"Текст должен содержать буквы или цифры и быть не длиннее {limit} символов.");
        return ReferenceKeys.Clean(value);
    }
}
