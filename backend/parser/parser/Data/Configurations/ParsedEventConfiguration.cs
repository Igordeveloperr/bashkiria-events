using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bashkiria.Events.Parser.Data.Configurations;

public sealed class ParsedEventConfiguration : IEntityTypeConfiguration<ParsedEvent>
{
    public void Configure(EntityTypeBuilder<ParsedEvent> builder)
    {
        builder.ToTable("parser_events");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => item.DedupKey).IsUnique().HasDatabaseName("parser_events_dedup_key");
        builder.HasIndex(item => new { item.Status, item.StartDate }).HasDatabaseName("parser_events_status_date_idx");
        builder.Property(item => item.Category).HasConversion<string>();
        builder.Property(item => item.Status).HasConversion<string>();
        builder.Property(item => item.SourceStatus).HasConversion<string>();
        builder.Property(item => item.SourceType).HasConversion<string>();
        builder.Property(item => item.Tags).HasColumnType("text[]");
        builder.Property(item => item.IssuesJson).HasColumnType("jsonb");
        builder.Property(item => item.PriceMin).HasPrecision(12, 2);
        builder.Property(item => item.PriceMax).HasPrecision(12, 2);
        builder.Property(item => item.Confidence).HasPrecision(4, 3);
        builder.Property(item => item.Latitude).HasPrecision(10, 7);
        builder.Property(item => item.Longitude).HasPrecision(10, 7);
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => new { item.CityId, item.VenueId }).HasDatabaseName("parser_events_reference_idx");
        builder.HasOne<ReferenceCity>().WithMany().HasForeignKey(item => item.CityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReferenceVenue>().WithMany().HasForeignKey(item => new { item.VenueId, item.CityId })
            .HasPrincipalKey(item => new { item.Id, item.CityId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EventSourceConfiguration : IEntityTypeConfiguration<EventSourceRecord>
{
    public void Configure(EntityTypeBuilder<EventSourceRecord> builder)
    {
        builder.ToTable("parser_event_sources");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => item.IdentityKey).IsUnique().HasDatabaseName("parser_event_sources_identity_key");
        builder.Property(item => item.CandidateJson).HasColumnType("jsonb");
        builder.Property(item => item.SourceType).HasConversion<string>();
        builder.HasIndex(item => item.EventId).HasDatabaseName("parser_event_sources_event_idx");
        builder.HasIndex(item => item.RawDocumentId).HasDatabaseName("parser_event_sources_raw_idx");
        builder.HasOne(item => item.Event).WithMany(item => item.Sources).HasForeignKey(item => item.EventId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.RawDocument).WithMany().HasForeignKey(item => item.RawDocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CollectionRunConfiguration : IEntityTypeConfiguration<CollectionRun>
{
    public void Configure(EntityTypeBuilder<CollectionRun> builder)
    {
        builder.ToTable("parser_collection_runs");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Status).HasConversion<string>();
        builder.Property(item => item.ReportJson).HasColumnType("jsonb");
        builder.HasIndex(item => item.SourceId).IsUnique().HasFilter("status IN ('Queued', 'Running')")
            .HasDatabaseName("parser_runs_one_active_source");
        builder.HasIndex(item => new { item.Status, item.CreatedAt }).HasDatabaseName("parser_runs_queue_idx");
        builder.HasIndex(item => item.ScheduleKey).IsUnique().HasDatabaseName("parser_runs_schedule_key");
    }
}

public sealed class EventAuditConfiguration : IEntityTypeConfiguration<EventAuditEntry>
{
    public void Configure(EntityTypeBuilder<EventAuditEntry> builder)
    {
        builder.ToTable("parser_event_audit");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.SnapshotJson).HasColumnType("jsonb");
        builder.HasIndex(item => new { item.EventId, item.CreatedAt }).HasDatabaseName("parser_event_audit_event_idx");
        builder.HasOne<ParsedEvent>().WithMany().HasForeignKey(item => item.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
