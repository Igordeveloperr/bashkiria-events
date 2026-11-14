using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.References;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Parser.Api;

public static class ReferenceEndpoints
{
    public static void MapReferenceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reference").WithTags("Reference");
        group.MapGet("/cities", async (string? q, int? page, int? pageSize, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            var (number, size) = Pagination(page, pageSize);
            var key = ReferenceKeys.City(q);
            await using var db = await contexts.CreateDbContextAsync(token);
            var query = db.Cities.AsNoTracking().Where(item => key == "" || item.NormalizedName.Contains(key) ||
                db.CityAliases.Any(alias => alias.CityId == item.Id && alias.NormalizedName.Contains(key)));
            var count = await query.CountAsync(token);
            var cities = await query.OrderBy(item => item.Name).ThenBy(item => item.Id).Skip((number - 1) * size).Take(size).ToListAsync(token);
            var ids = cities.Select(item => item.Id).ToArray();
            var aliases = await db.CityAliases.AsNoTracking().Where(item => ids.Contains(item.CityId)).OrderBy(item => item.Name).ToListAsync(token);
            return Results.Ok(new PageResponse<CityResponse>(count, number, size, cities.Select(item => new CityResponse(item.Id, item.Name,
                aliases.Where(alias => alias.CityId == item.Id).Select(alias => new ReferenceAliasResponse(alias.Id, alias.Name)).ToArray())).ToArray()));
        }).Produces<PageResponse<CityResponse>>();

        group.MapGet("/venues", async (Guid? cityId, string? q, int? page, int? pageSize, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            var (number, size) = Pagination(page, pageSize);
            var key = ReferenceKeys.Name(q);
            await using var db = await contexts.CreateDbContextAsync(token);
            var query = db.Venues.AsNoTracking().Where(item => !cityId.HasValue || item.CityId == cityId)
                .Where(item => key == "" || item.NormalizedName.Contains(key) || db.VenueAliases.Any(alias => alias.VenueId == item.Id && alias.NormalizedName.Contains(key)));
            var count = await query.CountAsync(token);
            var venues = await query.OrderBy(item => item.Name).ThenBy(item => item.Id).Skip((number - 1) * size).Take(size).ToListAsync(token);
            var ids = venues.Select(item => item.Id).ToArray();
            var aliases = await db.VenueAliases.AsNoTracking().Where(item => ids.Contains(item.VenueId)).OrderBy(item => item.Name).ThenBy(item => item.Address).ToListAsync(token);
            return Results.Ok(new PageResponse<VenueResponse>(count, number, size, venues.Select(item => new VenueResponse(item.Id, item.CityId, item.Name, item.Address,
                aliases.Where(alias => alias.VenueId == item.Id).Select(alias => new ReferenceAliasResponse(alias.Id, alias.Name, alias.Address)).ToArray())).ToArray()));
        }).Produces<PageResponse<VenueResponse>>();

        group.MapGet("/cities/{id:guid}", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var city = await db.Cities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (city is null) return Results.NotFound();
            var aliases = await db.CityAliases.AsNoTracking().Where(item => item.CityId == id).OrderBy(item => item.Name)
                .Select(item => new ReferenceAliasResponse(item.Id, item.Name, null)).ToListAsync(token);
            return Results.Ok(new CityResponse(city.Id, city.Name, aliases));
        }).Produces<CityResponse>();
        group.MapGet("/venues/{id:guid}", async (Guid id, IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (venue is null) return Results.NotFound();
            var aliases = await db.VenueAliases.AsNoTracking().Where(item => item.VenueId == id).OrderBy(item => item.Name).ThenBy(item => item.Address)
                .Select(item => new ReferenceAliasResponse(item.Id, item.Name, item.Address)).ToListAsync(token);
            return Results.Ok(new VenueResponse(venue.Id, venue.CityId, venue.Name, venue.Address, aliases));
        }).Produces<VenueResponse>();

        group.MapGet("/categories", () => Results.Ok(Enum.GetValues<EventCategory>().Select(category => new { id = category, name = category switch
        {
            EventCategory.Concert => "Концерт", EventCategory.Theatre => "Театр", EventCategory.Exhibition => "Выставка",
            EventCategory.Festival => "Фестиваль", EventCategory.Cinema => "Кино", EventCategory.Excursion => "Экскурсия",
            EventCategory.Workshop => "Мастер-класс", EventCategory.Lecture => "Лекция", _ => "Другое"
        }})));

        group.MapPost("/cities", async (CreateCityRequest request, ReferenceCatalog catalog, CancellationToken token) =>
        {
            var id = await catalog.CreateCityAsync(request, token);
            return Results.Created($"/api/v1/parser/reference/cities/{id}", new { id });
        });
        group.MapPost("/venues", async (CreateVenueRequest request, ReferenceCatalog catalog, CancellationToken token) =>
        {
            var id = await catalog.CreateVenueAsync(request, token);
            return Results.Created($"/api/v1/parser/reference/venues/{id}", new { id });
        });
        group.MapPost("/cities/{id:guid}/aliases", async (Guid id, CreateCityAliasRequest request, ReferenceCatalog catalog, CancellationToken token) =>
            Results.Created($"/api/v1/parser/reference/cities/{id}", new { id = await catalog.AddCityAliasAsync(id, request, token) }));
        group.MapPost("/venues/{id:guid}/aliases", async (Guid id, CreateVenueAliasRequest request, ReferenceCatalog catalog, CancellationToken token) =>
            Results.Created($"/api/v1/parser/reference/venues/{id}", new { id = await catalog.AddVenueAliasAsync(id, request, token) }));
        group.MapPost("/normalize", async (int? page, int? pageSize, ReferenceEventNormalizer normalizer, CancellationToken token) =>
        {
            var (number, size) = Pagination(page, pageSize);
            return Results.Ok(await normalizer.NormalizePageAsync(number, size, token));
        });
    }

    private static (int Number, int Size) Pagination(int? page, int? pageSize)
    {
        var number = page ?? 1;
        var size = pageSize ?? 30;
        if (number is < 1 or > 100000 || size is < 1 or > 100) throw new ArgumentException("Некорректная пагинация.");
        return (number, size);
    }
}
