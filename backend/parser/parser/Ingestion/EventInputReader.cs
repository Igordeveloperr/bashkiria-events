using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Contracts;
using CsvHelper;
using CsvHelper.Configuration;

namespace Bashkiria.Events.Parser.Ingestion;

public sealed record InputRow(int Row, EventInput? Value, IReadOnlyList<InputError> Errors);

public sealed class EventInputReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly Dictionary<string, PropertyInfo> CsvFields = typeof(EventInput).GetProperties()
        .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InputRow> ReadJson(string text, int maxRows)
    {
        using var document = ParseJson(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ParserValidationException("JSON импорта должен содержать массив мероприятий.");
        var total = document.RootElement.GetArrayLength();
        ValidateCount(total, maxRows);
        return document.RootElement.EnumerateArray().Select((element, index) => ReadObject(element, index + 1)).ToArray();
    }

    public EventInput ReadManual(string text)
    {
        using var document = ParseJson(text);
        var row = ReadObject(document.RootElement, 1);
        if (row.Value is null) throw new EventInputValidationException(row.Errors);
        return row.Value;
    }

    public IReadOnlyList<InputRow> ReadCsv(string text, int maxRows)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            DetectDelimiterValues = [",", ";"],
            TrimOptions = TrimOptions.None,
            DetectColumnCountChanges = true,
            ExceptionMessagesContainRawData = false,
            MaxFieldSize = 65536
        };
        using var input = new StringReader(text.TrimStart('\uFEFF'));
        using var csv = new CsvReader(input, configuration);
        try
        {
            if (!csv.Read()) throw new ParserValidationException("CSV пустой: требуется строка заголовков.");
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? [];
            var properties = new List<PropertyInfo>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                var key = header.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
                if (!CsvFields.TryGetValue(key, out var property) || !names.Add(property.Name))
                    throw new ParserValidationException("CSV содержит неизвестные или повторяющиеся заголовки. Используйте поля контракта EventInput.");
                properties.Add(property);
            }
            if (!names.Contains(nameof(EventInput.Title))) throw new ParserValidationException("В CSV обязателен столбец title.");
            var rows = new List<InputRow>();
            while (csv.Read())
            {
                if (rows.Count >= maxRows) throw new ParserValidationException($"Импорт ограничен {maxRows} записями.");
                var record = new JsonObject();
                var errors = new List<InputError>();
                for (var index = 0; index < properties.Count; index++)
                {
                    var property = properties[index];
                    var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    var value = csv.GetField(index);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                    if (type == typeof(string[])) record[name] = new JsonArray(value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
                    else if (type == typeof(bool))
                    {
                        if (bool.TryParse(value.Trim(), out var boolean)) record[name] = boolean;
                        else errors.Add(new(name, "invalid_format", "Укажите true или false."));
                    }
                    else if (type == typeof(decimal))
                    {
                        var number = string.Concat(value.Where(character => !char.IsWhiteSpace(character))).Replace(',', '.');
                        if (decimal.TryParse(number, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) record[name] = amount;
                        else errors.Add(new(name, "invalid_format", "Некорректное десятичное число."));
                    }
                    else if (type == typeof(int))
                    {
                        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) record[name] = integer;
                        else errors.Add(new(name, "invalid_format", "Некорректное целое число."));
                    }
                    else if (type == typeof(TimeOnly))
                    {
                        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                            record[name] = time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
                        else errors.Add(new(name, "invalid_format", "Укажите время HH:mm или HH:mm:ss."));
                    }
                    else record[name] = value.Trim();
                }
                var rowNumber = rows.Count + 2;
                rows.Add(errors.Count > 0 ? new(rowNumber, null, errors) : ReadObject(JsonSerializer.SerializeToElement(record), rowNumber));
            }
            ValidateCount(rows.Count, maxRows);
            return rows;
        }
        catch (CsvHelperException) { throw new ParserValidationException("Некорректная структура CSV: проверьте кавычки, разделители и количество столбцов."); }
    }

    private static InputRow ReadObject(JsonElement element, int row)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new(row, null, [new("", "invalid_record", "Запись должна быть JSON-объектом.")]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.EnumerateObject().Any(property => !names.Add(property.Name)))
            return new(row, null, [new("", "duplicate_field", "В записи повторяется имя поля.")]);
        try { return new(row, element.Deserialize<EventInput>(JsonOptions)!, []); }
        catch (JsonException error)
        {
            return new(row, null, [new(error.Path?.TrimStart('$', '.') ?? "", "invalid_format", "Неизвестное поле или неверный тип / формат значения.")]);
        }
    }

    private static JsonDocument ParseJson(string text)
    {
        try { return JsonDocument.Parse(text.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw new ParserValidationException("Некорректный JSON. Проверьте структуру документа."); }
    }

    private static void ValidateCount(int count, int maxRows)
    {
        if (count == 0) throw new ParserValidationException("В файле нет мероприятий.");
        if (count > maxRows) throw new ParserValidationException($"Импорт ограничен {maxRows} записями.");
    }
}
