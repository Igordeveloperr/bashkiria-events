namespace Bashkiria.Events.Interaction.Services;

public static class DigestSchedule
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg");
    public static DateOnly LocalDate(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
    public static bool CanGenerate(DateTimeOffset now, int hour)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        return local.Hour >= hour && local.Hour < 21;
    }
    public static DateTimeOffset NextDay(DateTimeOffset now, int hour) => new(TimeZoneInfo.ConvertTimeToUtc(
        LocalDate(now).AddDays(1).ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified), Zone));
}
