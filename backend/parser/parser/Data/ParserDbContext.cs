using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Bashkiria.Events.Parser.Data;

public sealed class ParserDbContext(DbContextOptions<ParserDbContext> options) : DbContext(options)
{
    public DbSet<RawDocument> RawDocuments => Set<RawDocument>();
    public DbSet<ParsedEvent> Events => Set<ParsedEvent>();
    public DbSet<EventSourceRecord> EventSources => Set<EventSourceRecord>();
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();
    public DbSet<EventAuditEntry> AuditEntries => Set<EventAuditEntry>();
    public DbSet<ReferenceCity> Cities => Set<ReferenceCity>();
    public DbSet<CityAlias> CityAliases => Set<CityAlias>();
    public DbSet<ReferenceVenue> Venues => Set<ReferenceVenue>();
    public DbSet<VenueAlias> VenueAliases => Set<VenueAlias>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ParserDbContext).Assembly);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
