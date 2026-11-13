using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Processing;

public sealed class CandidateNormalizer(TimeProvider clock)
{
    public NormalizedCandidate Normalize(string sourceId, EventCandidate input)
    {
        var candidate = input with
        {
            Title = Clean(input.Title),
            City = Regex.Replace(Clean(input.City), @"^(?:г\.\s*|г\s+|город\s+)", "", RegexOptions.IgnoreCase),
            Venue = Clean(input.Venue),
            Address = Clean(input.Address),
            Description = Clean(input.Description),
            SourceUrl = SourceUrls.Canonical(input.SourceUrl)
        };
        var issues = candidate.Issues.ToList();
        if (candidate.Latitude is < -90 or > 90 || candidate.Longitude is < -180 or > 180)
        {
            issues.Add(new("invalid_coordinates", "Координаты площадки некорректны."));
            candidate = candidate with { Latitude = null, Longitude = null };
        }
        if (string.IsNullOrWhiteSpace(candidate.Title)) issues.Add(new("missing_title", "Название не указано."));
        if (string.IsNullOrWhiteSpace(candidate.City)) issues.Add(new("missing_city", "Населённый пункт не указан."));
        if (string.IsNullOrWhiteSpace(candidate.Venue)) issues.Add(new("missing_venue", "Площадка не указана."));
        if (string.IsNullOrWhiteSpace(candidate.Address)) issues.Add(new("missing_address", "Адрес площадки не указан."));
        if (candidate.StartDate is null) issues.Add(new("missing_start_date", "Дата начала не указана."));
        if (candidate.StartTime is null) issues.Add(new("missing_start_time", "Время начала не указано."));
        if (candidate.PriceMin is null && candidate.IsFree != true) issues.Add(new("unknown_price", "Цена не указана."));
        if (candidate.AgeLimit is null) issues.Add(new("unknown_age", "Возрастное ограничение не указано."));
        if (candidate.AgeLimit is not null && candidate.AgeLimit is not (0 or 6 or 12 or 16 or 18))
        {
            issues.Add(new("invalid_age", "Некорректное возрастное ограничение."));
            candidate = candidate with { AgeLimit = null };
        }
        if (candidate.PriceMin < 0 || candidate.PriceMax < 0 || candidate.PriceMax < candidate.PriceMin)
        {
            issues.Add(new("invalid_price", "Некорректный диапазон цен."));
            candidate = candidate with { PriceMin = null, PriceMax = null, IsFree = null };
        }
        if (candidate.IsFree == true && (candidate.PriceMin > 0 || candidate.PriceMax > 0))
        {
            issues.Add(new("price_conflict", "Бесплатный вход противоречит указанной цене."));
            candidate = candidate with { IsFree = null };
        }
        var start = ToUtc(candidate.StartDate, candidate.StartTime);
        var end = ToUtc(candidate.EndDate, candidate.EndTime);
        if (candidate.EndDate < candidate.StartDate || (end.HasValue && start.HasValue && end < start))
        {
            issues.Add(new("invalid_end", "Окончание предшествует началу."));
            candidate = candidate with { EndDate = null, EndTime = null };
            end = null;
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);
        if ((candidate.EndDate ?? candidate.StartDate) < today)
            issues.Add(new("expired", "Мероприятие уже завершилось."));
        if (candidate.SourceStatus is SourceEventStatus.Unknown or SourceEventStatus.Postponed)
            issues.Add(new("unconfirmed_status", "Статус мероприятия требует подтверждения."));
        issues = issues.DistinctBy(issue => issue.Code).ToList();
        candidate = candidate with { Issues = issues };

        var identity = Hash(sourceId + "|" + (candidate.ExternalId ?? candidate.SourceUrl.AbsoluteUri));
        var normalizedTitle = NormalizeText(candidate.Title);
        var dedupKey = candidate.StartDate.HasValue && candidate.StartTime.HasValue &&
            !string.IsNullOrWhiteSpace(candidate.City) && !string.IsNullOrWhiteSpace(candidate.Venue)
            ? Hash(string.Join('|', normalizedTitle, NormalizeText(candidate.City), NormalizeVenue(candidate.Venue),
                candidate.StartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                candidate.StartTime.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture))) : identity;
        var json = ParserJson.Serialize(candidate with { RawDocumentId = null });
        var confidence = Math.Clamp(0.95m - issues.Count * 0.1m, 0.1m, 0.95m);
        return new(candidate, identity, dedupKey, normalizedTitle, Hash(json), json, confidence, start, end);
    }

    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg");
    public static DateTimeOffset? ToUtc(DateOnly? date, TimeOnly? time) => date.HasValue && time.HasValue
        ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.Value.ToDateTime(time.Value, DateTimeKind.Unspecified), Zone)) : null;
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string NormalizeText(string? value) => Regex.Replace(Clean(value).Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();
    private static string Clean(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string NormalizeVenue(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized is "гкз башкортостан" or "государственный концертный зал башкортостан" ? "гкз башкортостан" : normalized;
    }
}

public sealed record NormalizedCandidate(EventCandidate Candidate, string IdentityKey, string DedupKey,
    string NormalizedTitle, string DataHash, string CandidateJson, decimal Confidence,
    DateTimeOffset? StartUtc, DateTimeOffset? EndUtc);
