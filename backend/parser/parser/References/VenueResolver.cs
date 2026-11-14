using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Parser.References;

public sealed class VenueResolver
{
    public static readonly string[] IssueCodes = ["unknown_city", "unknown_venue", "ambiguous_venue", "venue_address_conflict", "reference_duplicate"];

    public async Task<EventCandidate> ResolveAsync(ParserDbContext db, EventCandidate input, CancellationToken token)
    {
        var issues = input.Issues.Where(issue => !IssueCodes.Contains(issue.Code)).ToList();
        var cityKey = ReferenceKeys.City(input.City);
        ReferenceCity? city = null;
        ReferenceVenue? venue = null;
        if (input.VenueId.HasValue)
        {
            venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(item => item.Id == input.VenueId, token)
                ?? throw new ParserValidationException("venueId отсутствует в справочнике.");
            if (input.CityId.HasValue && input.CityId != venue.CityId)
                throw new ParserValidationException("Площадка не принадлежит указанному cityId.");
            city = await db.Cities.AsNoTracking().SingleAsync(item => item.Id == venue.CityId, token);
        }
        else if (input.CityId.HasValue)
            city = await db.Cities.AsNoTracking().SingleOrDefaultAsync(item => item.Id == input.CityId, token)
                ?? throw new ParserValidationException("cityId отсутствует в справочнике.");
        else if (cityKey.Length > 0)
            city = await (from alias in db.CityAliases.AsNoTracking()
                          join item in db.Cities.AsNoTracking() on alias.CityId equals item.Id
                          where alias.NormalizedName == cityKey select item).SingleOrDefaultAsync(token);

        if (city is null)
        {
            if (cityKey.Length > 0) issues.Add(new("unknown_city", "Населённый пункт не найден в справочнике."));
            return input with { CityId = null, VenueId = null, Issues = issues };
        }

        var nameKey = ReferenceKeys.Name(input.Venue);
        var addressKey = ReferenceKeys.Address(input.Address);
        if (venue is null && nameKey.Length > 0)
        {
            var aliases = await db.VenueAliases.AsNoTracking().Where(item => item.CityId == city.Id && item.NormalizedName == nameKey)
                .ToListAsync(token);
            var exactIds = aliases.Where(item => addressKey.Length > 0 && item.NormalizedAddress == addressKey)
                .Select(item => item.VenueId).Distinct().ToArray();
            var ids = exactIds.Length > 0 ? exactIds : aliases.Select(item => item.VenueId).Distinct().ToArray();
            var venues = await db.Venues.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(token);
            var matches = exactIds.Length > 0 || addressKey.Length == 0 ? venues :
                venues.Where(item => item.NormalizedAddress == addressKey).ToList();
            if (matches.Count == 1) venue = matches[0];
            else if (matches.Count > 1) issues.Add(new("ambiguous_venue", "Название соответствует нескольким площадкам. Уточните адрес или venueId."));
            else if (venues.Count > 0) issues.Add(new("venue_address_conflict", "Адрес источника расходится со справочником. Добавьте проверенный вариант адреса или уточните площадку."));
            else issues.Add(new("unknown_venue", "Площадка не найдена в справочнике."));
        }
        return input with
        {
            CityId = city.Id, City = city.Name,
            VenueId = venue?.Id, Venue = venue?.Name ?? input.Venue,
            Address = venue?.Address ?? input.Address, Issues = issues
        };
    }
}
