using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bashkiria.Events.Parser.Data.Configurations;

public sealed class RawDocumentConfiguration : IEntityTypeConfiguration<RawDocument>
{
    public void Configure(EntityTypeBuilder<RawDocument> builder)
    {
        builder.ToTable("parser_raw_documents");
        builder.HasKey(document => document.Id).HasName("parser_raw_documents_pkey");
        builder.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(document => document.SourceId).HasColumnName("source_id");
        builder.Property(document => document.SourceType).HasConversion<string>();
        builder.Property(document => document.Url).HasColumnName("url")
            .HasConversion(url => url.AbsoluteUri, value => new Uri(value));
        builder.Property(document => document.FetchedAt).HasColumnName("fetched_at");
        builder.Property(document => document.HttpStatus).HasColumnName("http_status");
        builder.Property(document => document.Content).HasColumnName("content");
        builder.Property(document => document.ContentHash).HasColumnName("content_hash");
        builder.HasIndex(document => new { document.SourceId, document.Url, document.FetchedAt })
            .IsDescending(false, false, true).HasDatabaseName("parser_raw_source_url_fetched_idx");
    }
}
