using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Models;

namespace Bashkiria.Events.Parser.Parsing;

public static partial class EventFields
{
    public static SourceEventStatus Status(JsonNode? value) => StructuredDataReader.Text(value)?.Split('/').Last() switch
    {
        "EventScheduled" => SourceEventStatus.Scheduled,
        "EventCancelled" => SourceEventStatus.Cancelled,
        "EventPostponed" => SourceEventStatus.Postponed,
        "EventRescheduled" => SourceEventStatus.Rescheduled,
        _ => SourceEventStatus.Unknown
    };

    public static EventCategory Category(string text)
    {
        text = text.ToLowerInvariant();
        if (text.Contains("спектак") || text.Contains("балет") || text.Contains("theater")) return EventCategory.Theatre;
        if (text.Contains("концерт") || text.Contains("concert") || text.Contains("music")) return EventCategory.Concert;
        if (text.Contains("выстав") || text.Contains("exhibition")) return EventCategory.Exhibition;
        if (text.Contains("фестив") || text.Contains("festival")) return EventCategory.Festival;
        if (text.Contains("мастер-класс")) return EventCategory.Workshop;
        if (text.Contains("экскурс")) return EventCategory.Excursion;
        if (text.Contains("лекц")) return EventCategory.Lecture;
        if (text.Contains("кино") || text.Contains("screening")) return EventCategory.Cinema;
        return EventCategory.Other;
    }

    public static int? Age(string? text)
    {
        var match = AgePattern().Match(text ?? string.Empty);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public static decimal? Number(JsonNode? node)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<decimal>(out var number)) return number;
        var raw = StructuredDataReader.Text(node)?.Replace(" ", "").Replace("\u00a0", "").Replace(',', '.');
        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    public static (decimal? Min, decimal? Max, bool? Free) Price(JsonObject? data, string? visibleText = null)
    {
        var free = data?["isAccessibleForFree"]?.ToString();
        if (string.Equals(free, "true", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(visibleText ?? "", @"^\s*(?:вход\s+свободный|бесплатно)\s*[!.]?\s*$", RegexOptions.IgnoreCase))
            return (0, 0, true);

        var offers = StructuredDataReader.Values(data?["offers"]).OfType<JsonObject>()
            .Where(offer => StructuredDataReader.Text(offer["priceCurrency"]) is null or "RUB").ToArray();
        var lows = offers.Select(offer => Number(offer["lowPrice"] ?? offer["price"])).Where(value => value >= 0).Select(value => value!.Value).ToArray();
        var highs = offers.Select(offer => Number(offer["highPrice"] ?? offer["price"])).Where(value => value >= 0).Select(value => value!.Value).ToArray();
        if (lows.Length > 0)
        {
            var min = lows.Min();
            decimal? max = highs.Length > 0 ? highs.Max() : null;
            return (min, max, max == 0 ? true : min > 0 ? false : null);
        }
        var match = PricePattern().Match(visibleText ?? "");
        if (!match.Success) return (null, null, null);
        var lower = decimal.Parse(match.Groups[1].Value.Replace(" ", "").Replace("\u00a0", ""), CultureInfo.InvariantCulture);
        decimal? upper = match.Groups[2].Success ? decimal.Parse(match.Groups[2].Value.Replace(" ", "").Replace("\u00a0", ""), CultureInfo.InvariantCulture) : null;
        return (lower, upper, lower > 0 ? false : null);
    }

    public static (DateOnly? Date, TimeOnly? Time) Timestamp(string? raw)
    {
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (date, null);
        if (raw is null || !TimestampPattern().IsMatch(raw)) return (null, null);
        DateTime local;
        if (raw.EndsWith('Z') || Regex.IsMatch(raw, @"[+-]\d{2}:\d{2}$"))
        {
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned)) return (null, null);
            local = TimeZoneInfo.ConvertTime(zoned, TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg")).DateTime;
        }
        else if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) return (null, null);
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    [GeneratedRegex(@"(?:категория\s*:?\s*|возраст(?:ное ограничение)?\s*:?\s*|(?<!\d))(0|6|12|16|18)\s*\+", RegexOptions.IgnoreCase)]
    private static partial Regex AgePattern();
    [GeneratedRegex(@"(?:от\s*)?(\d[\d \u00a0]*)(?:\s*(?:до|[-–—])\s*(\d[\d \u00a0]*))?\s*(?:₽|руб)", RegexOptions.IgnoreCase)]
    private static partial Regex PricePattern();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})?$")]
    private static partial Regex TimestampPattern();
}
