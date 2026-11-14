using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bashkiria.Events.Parser.Data.Configurations;

public sealed class ReferenceCityConfiguration : IEntityTypeConfiguration<ReferenceCity>
{
    public void Configure(EntityTypeBuilder<ReferenceCity> builder)
    {
        builder.ToTable("parser_cities");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => item.NormalizedName).IsUnique().HasDatabaseName("parser_cities_name_key");
    }
}

public sealed class CityAliasConfiguration : IEntityTypeConfiguration<CityAlias>
{
    public void Configure(EntityTypeBuilder<CityAlias> builder)
    {
        builder.ToTable("parser_city_aliases");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => item.NormalizedName).IsUnique().HasDatabaseName("parser_city_aliases_name_key");
        builder.HasOne<ReferenceCity>().WithMany().HasForeignKey(item => item.CityId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ReferenceVenueConfiguration : IEntityTypeConfiguration<ReferenceVenue>
{
    public void Configure(EntityTypeBuilder<ReferenceVenue> builder)
    {
        builder.ToTable("parser_venues");
        builder.HasKey(item => item.Id);
        builder.HasAlternateKey(item => new { item.Id, item.CityId }).HasName("parser_venues_id_city_key");
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => new { item.CityId, item.NormalizedName, item.NormalizedAddress }).IsUnique()
            .HasDatabaseName("parser_venues_identity_key");
        builder.HasOne<ReferenceCity>().WithMany().HasForeignKey(item => item.CityId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class VenueAliasConfiguration : IEntityTypeConfiguration<VenueAlias>
{
    public void Configure(EntityTypeBuilder<VenueAlias> builder)
    {
        builder.ToTable("parser_venue_aliases");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => new { item.CityId, item.NormalizedName, item.NormalizedAddress }).IsUnique()
            .HasDatabaseName("parser_venue_aliases_identity_key");
        builder.HasOne<ReferenceVenue>().WithMany().HasForeignKey(item => new { item.VenueId, item.CityId })
            .HasPrincipalKey(item => new { item.Id, item.CityId }).OnDelete(DeleteBehavior.Restrict);
    }
}
