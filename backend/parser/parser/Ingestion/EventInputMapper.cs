using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Processing;

namespace Bashkiria.Events.Parser.Ingestion;

public sealed class EventInputMapper(CandidateNormalizer normalizer)
{
    public IReadOnlyList<InputError> Validate(EventInput input)
    {
        var errors = new List<InputError>();
        if (string.IsNullOrWhiteSpace(input.Title)) errors.Add(new("title", "required", "Укажите название мероприятия."));
        CheckText(input.Title, "title", 500);
        CheckText(input.ExternalId, "externalId", 200);
        CheckText(input.City, "city", 200);
        CheckText(input.Venue, "venue", 500);
        CheckText(input.Address, "address", 2000);
        CheckText(input.Description, "description", 50000);
        CheckText(input.Language, "language", 100);
        if (!Enum.IsDefined(input.Category)) errors.Add(new("category", "invalid_enum", "Неизвестная категория."));
        if (!Enum.IsDefined(input.SourceStatus)) errors.Add(new("sourceStatus", "invalid_enum", "Неизвестный статус источника."));
        CheckUrl(input.SourceUrl, "sourceUrl"); CheckUrl(input.OrgLink, "orgLink"); CheckUrl(input.ImageUrl, "imageUrl");
        CheckPrice(input.PriceMin, "priceMin"); CheckPrice(input.PriceMax, "priceMax");
        if (input.PriceMax < input.PriceMin) errors.Add(new("priceMax", "invalid_range", "Максимальная цена меньше минимальной."));
        if (input.IsFree == true && (input.PriceMin > 0 || input.PriceMax > 0) || input.IsFree == false && input.PriceMin == 0 && input.PriceMax == 0)
            errors.Add(new("isFree", "price_conflict", "Признак бесплатного входа противоречит цене."));
        if (input.AgeLimit.HasValue && input.AgeLimit is not (0 or 6 or 12 or 16 or 18))
            errors.Add(new("ageLimit", "invalid_age", "Допустимый возраст: 0, 6, 12, 16 или 18."));
        CheckCoordinate(input.Latitude, "latitude", 90);
        CheckCoordinate(input.Longitude, "longitude", 180);
        var endDate = input.EndDate ?? (input.EndTime.HasValue ? input.StartDate : null);
        if (input.EndTime.HasValue && !endDate.HasValue)
            errors.Add(new("endDate", "required", "Для времени окончания нужна дата окончания или начала."));
        if (endDate < input.StartDate || endDate == input.StartDate && input.EndTime < input.StartTime)
            errors.Add(new("endDate", "invalid_end", "Окончание предшествует началу."));
        if (input.Tags is { Length: > 50 }) errors.Add(new("tags", "too_many", "Допускается не более 50 тегов."));
        if (input.Tags?.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 100 || tag.Contains('\0')) == true)
            errors.Add(new("tags", "invalid_tag", "Теги должны быть непустыми и не длиннее 100 символов."));
        return errors;

        void CheckText(string? value, string field, int limit)
        {
            if (value?.Length > limit) errors.Add(new(field, "too_long", $"Максимальная длина — {limit} символов."));
            if (value?.Contains('\0') == true) errors.Add(new(field, "invalid_text", "Текст не должен содержать нулевые символы."));
        }
        void CheckUrl(Uri? value, string field)
        {
            if (value is not null && (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(value.UserInfo) || value.AbsoluteUri.Length > 2048))
                errors.Add(new(field, "invalid_url", "Укажите абсолютный HTTP(S)-адрес без учётных данных, до 2048 символов."));
        }
        void CheckPrice(decimal? value, string field)
        {
            if (value < 0 || value > 9999999999.99m || value.HasValue && decimal.Round(value.Value, 2) != value)
                errors.Add(new(field, "invalid_price", "Цена должна быть от 0 до 9999999999.99 с точностью до копеек."));
        }
        void CheckCoordinate(decimal? value, string field, int limit)
        {
            if (value.HasValue && (value < -limit || value > limit || decimal.Round(value.Value, 7) != value))
                errors.Add(new(field, "invalid_coordinate", $"Координата должна быть в пределах ±{limit}, до 7 знаков после точки."));
        }
    }

    public EventCandidate Map(EventInput input, string sourceId, string? externalId = null)
    {
        var errors = Validate(input);
        if (errors.Count > 0) throw new EventInputValidationException(errors);
        var contentKey = CandidateNormalizer.Hash(ParserJson.Serialize(input));
        var candidate = new EventCandidate
        {
            Title = input.Title!, SourceUrl = input.SourceUrl ?? new Uri("urn:bashkiria-events:input:" + contentKey),
            StartDate = input.StartDate, StartTime = input.StartTime,
            EndDate = input.EndDate ?? (input.EndTime.HasValue ? input.StartDate : null), EndTime = input.EndTime,
            City = input.City, Venue = input.Venue, Address = input.Address, Description = input.Description,
            Category = input.Category, Tags = input.Tags?.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
            PriceMin = input.IsFree == true ? 0 : input.PriceMin,
            PriceMax = input.IsFree == true ? 0 : input.PriceMax,
            IsFree = input.IsFree ?? (input.PriceMin == 0 && input.PriceMax == 0 ? true : input.PriceMin > 0 ? false : null),
            AgeLimit = input.AgeLimit, Latitude = input.Latitude, Longitude = input.Longitude,
            Language = input.Language, OrgLink = input.OrgLink, ImageUrl = input.ImageUrl, SourceStatus = input.SourceStatus,
            Issues = [new("input_requires_review", "Данные внесены вручную или из файла и требуют проверки модератором.")]
        };
        if (string.IsNullOrWhiteSpace(externalId)) externalId = input.ExternalId?.Trim();
        if (string.IsNullOrWhiteSpace(externalId))
        {
            var completeIdentity = input.StartDate.HasValue && input.StartTime.HasValue && !string.IsNullOrWhiteSpace(input.City) && !string.IsNullOrWhiteSpace(input.Venue);
            externalId = completeIdentity ? "dedup:" + normalizer.Normalize(sourceId, candidate).DedupKey : "content:" + contentKey;
        }
        return candidate with
        {
            ExternalId = externalId,
            SourceUrl = input.SourceUrl ?? new Uri("urn:bashkiria-events:record:" + CandidateNormalizer.Hash(sourceId + "|" + externalId))
        };
    }
}
