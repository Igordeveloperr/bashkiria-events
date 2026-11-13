using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;

namespace Bashkiria.Events.Parser.Parsing.Sources;

internal static class GkzScheduleReader
{
    // MEC uses shortened Russian month names that do not all match ru-RU's MMM format.
    private static readonly IReadOnlyDictionary<string, int> Months = new Dictionary<string, int>
    {
        ["янв"] = 1, ["фев"] = 2, ["мар"] = 3, ["апр"] = 4,
        ["май"] = 5, ["мая"] = 5, ["июн"] = 6, ["июл"] = 7,
        ["авг"] = 8, ["сен"] = 9, ["сент"] = 9, ["окт"] = 10,
        ["ноя"] = 11, ["дек"] = 12
    };

    public static Schedule Read(IDocument document, DateTime? calendarStart)
    {
        var issues = new List<ParsingIssue>();
        var visibleDates = new List<DateOnly>();
        var visibleTimes = new List<TimeOnly>();

        foreach (var element in document.QuerySelectorAll(".mec-single-event-date .mec-start-date-label"))
        {
            if (TryReadVisibleDate(element.TextContent, out var date))
                visibleDates.Add(date);
            else
                issues.Add(new("invalid_visible_date", "Не удалось разобрать дату в блоке расписания."));
        }

        foreach (var element in document.QuerySelectorAll(".mec-single-event-time dd abbr"))
        {
            if (TimeOnly.TryParseExact(element.TextContent.Trim(), ["H:mm", "HH:mm"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                visibleTimes.Add(time);
            else
                issues.Add(new("invalid_visible_time", "Не удалось разобрать время в блоке расписания."));
        }

        var structuredDates = ReadStructuredDates(document).Distinct().ToArray();
        var dates = visibleDates.Distinct().ToArray();
        var times = visibleTimes.Distinct().ToArray();

        if (dates.Length > 1 || times.Length > 1 || structuredDates.Length > 1)
            issues.Add(new("multiple_start_values", "В карточке указано несколько вариантов начала; требуется проверка."));

        DateOnly? calendarDate = calendarStart.HasValue ? DateOnly.FromDateTime(calendarStart.Value) : null;
        TimeOnly? calendarTime = calendarStart.HasValue ? TimeOnly.FromDateTime(calendarStart.Value) : null;
        DateOnly? visibleDate = dates.Length == 1 ? dates[0] : null;
        DateOnly? structuredDate = structuredDates.Length == 1 ? structuredDates[0] : null;

        // Prefer the schedule displayed to visitors. JSON-LD may provide only the date.
        var selectedDate = visibleDate ?? structuredDate ?? calendarDate;
        TimeOnly? selectedTime = times.Length == 1 ? times[0] : null;
        if (selectedTime is null && times.Length == 0 && selectedDate == calendarDate)
        {
            selectedTime = calendarTime;
            if (calendarTime.HasValue)
                issues.Add(new("calendar_time_unconfirmed",
                    "Время найдено только в календаре; необходимо подтвердить его по расписанию."));
        }

        if ((visibleDate.HasValue && structuredDate.HasValue && visibleDate != structuredDate) ||
            (selectedDate.HasValue && calendarDate.HasValue && selectedDate != calendarDate))
            issues.Add(new("start_date_conflict", "Дата в расписании, JSON-LD или календаре различается."));

        if (times.Length == 1 && calendarTime.HasValue && times[0] != calendarTime.Value)
            issues.Add(new("start_time_conflict",
                $"Время в расписании ({times[0]:HH:mm}) отличается от календаря ({calendarTime.Value:HH:mm})."));

        if (selectedDate is null)
            issues.Add(new("missing_start_date", "Дата начала не найдена."));
        if (selectedTime is null)
            issues.Add(new("missing_start_time", "Время начала не найдено."));

        return new(selectedDate, selectedTime, issues.ToArray());
    }

    private static bool TryReadVisibleDate(string text, out DateOnly date)
    {
        date = default;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !Months.TryGetValue(parts[0].TrimEnd('.').ToLowerInvariant(), out var month) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            parts[2].Length != 4 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return false;

        return DateOnly.TryParseExact($"{year:D4}-{month:D2}-{day:D2}", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static IEnumerable<DateOnly> ReadStructuredDates(IDocument document)
    {
        var dates = new List<DateOnly>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                Visit(json.RootElement, dates);
            }
            catch (JsonException)
            {
                // Invalid structured blocks must not prevent reading the visible schedule.
            }
        }
        return dates;
    }

    private static void Visit(JsonElement element, List<DateOnly> dates)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Visit(child, dates);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out var type) && IsEvent(type) &&
                element.TryGetProperty("startDate", out var start) &&
                start.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(start.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                dates.Add(date);

            // Do not combine schedules of nested related events with this card.
            if (element.TryGetProperty("@graph", out var graph)) Visit(graph, dates);
        }
    }

    private static bool IsEvent(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => type.GetString() is "Event" or "https://schema.org/Event" or "http://schema.org/Event",
        JsonValueKind.Array => type.EnumerateArray().Any(IsEvent),
        _ => false
    };

    internal sealed record Schedule(DateOnly? Date, TimeOnly? Time, IReadOnlyList<ParsingIssue> Issues);
}
