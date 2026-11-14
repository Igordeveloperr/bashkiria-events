using System.ComponentModel.DataAnnotations;

namespace Bashkiria.Events.Interaction.Configuration;

public sealed class InteractionOptions
{
    public string? ApiKey { get; set; }
    public string? AdminApiKey { get; set; }
    public string? DeliveryApiKey { get; set; }
    public bool WorkerEnabled { get; set; } = true;
    [Range(5, 3600)] public int PollSeconds { get; set; } = 60;
    [Range(1, 20)] public int DigestSize { get; set; } = 5;
    [Range(9, 20)] public int DigestHour { get; set; } = 9;
    [Range(1, 365)] public int HorizonDays { get; set; } = 30;
    [Range(1, 168)] public int FreshnessHours { get; set; } = 24;
    [Range(1, 50)] public int MaxSubscriptions { get; set; } = 20;
    [Range(1, 50)] public int FeedbackPerHour { get; set; } = 5;
    [Range(1, 365)] public int NotificationRetentionDays { get; set; } = 30;
    [Range(1, 365)] public int FeedbackRetentionDays { get; set; } = 90;
}
