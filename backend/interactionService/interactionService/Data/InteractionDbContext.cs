using System.Text.RegularExpressions;
using Bashkiria.Events.Interaction.Models;
using Microsoft.EntityFrameworkCore;

namespace Bashkiria.Events.Interaction.Data;

public sealed class InteractionDbContext(DbContextOptions<InteractionDbContext> options) : DbContext(options)
{
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<FeedbackReview> FeedbackReviews => Set<FeedbackReview>();
    public DbSet<CatalogCity> Cities => Set<CatalogCity>();
    public DbSet<CatalogEvent> CatalogEvents => Set<CatalogEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Recipient>(b =>
        {
            b.ToTable("interaction_recipients"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Channel).HasConversion<string>(); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.UserId, x.Channel }).IsUnique().HasDatabaseName("interaction_recipients_identity");
        });
        model.Entity<Subscription>(b =>
        {
            b.ToTable("interaction_subscriptions"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Category).HasConversion<string>(); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.RecipientId, x.FilterHash }).IsUnique().HasDatabaseName("interaction_subscriptions_filter");
            b.HasOne(x => x.Recipient).WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CatalogCity>().WithMany().HasForeignKey(x => x.CityId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Notification>(b =>
        {
            b.ToTable("interaction_notifications"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<string>(); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.Status, x.AvailableAt }).HasDatabaseName("interaction_notifications_queue");
            b.HasIndex(x => new { x.RecipientId, x.CreatedAt }).HasDatabaseName("interaction_notifications_inbox");
            b.HasOne<Recipient>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<NotificationEvent>(b =>
        {
            b.ToTable("interaction_notification_events"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.SubscriptionId, x.EventId }).IsUnique().HasDatabaseName("interaction_notification_events_once");
            b.HasOne<Notification>().WithMany(x => x.Events).HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Feedback>(b =>
        {
            b.ToTable("interaction_feedback"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasConversion<string>(); b.Property(x => x.Status).HasConversion<string>(); b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.UserId, x.RequestKey }).IsUnique().HasDatabaseName("interaction_feedback_request");
            b.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("interaction_feedback_user_date");
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("interaction_feedback_queue");
        });
        model.Entity<FeedbackReview>(b =>
        {
            b.ToTable("interaction_feedback_reviews"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PreviousStatus).HasConversion<string>(); b.Property(x => x.Status).HasConversion<string>();
            b.HasOne<Feedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CatalogCity>().ToTable("parser_cities");
        model.Entity<CatalogEvent>(b =>
        {
            b.ToTable("parser_events"); b.Property(x => x.Category).HasConversion<string>();
            b.Property(x => x.PriceMin).HasPrecision(12, 2); b.Property(x => x.PriceMax).HasPrecision(12, 2);
        });
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries().Any(entry => entry.Entity is CatalogCity or CatalogEvent &&
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Catalog projections are read-only.");
        return base.SaveChangesAsync(cancellationToken);
    }
}
