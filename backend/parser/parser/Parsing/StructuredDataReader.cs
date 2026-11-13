using AngleSharp.Dom;
using System.Text.Json.Nodes;

namespace Bashkiria.Events.Parser.Parsing;

public static class StructuredDataReader
{
    public static IReadOnlyList<JsonObject> ReadEvents(IDocument document)
    {
        var records = new List<JsonObject>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try { Walk(JsonNode.Parse(script.TextContent), records); }
            catch (System.Text.Json.JsonException) { /* Other blocks and microdata can still be valid. */ }
        }

        var references = records.Where(item => Text(item["@id"]) is not null && item.Count > 1)
            .GroupBy(item => Text(item["@id"])!).ToDictionary(group => group.Key, group => group.Last());
        var events = records.Where(item => IsEvent(item["@type"]))
            .Select(item => (JsonObject)Resolve(item, references, [], 0)!).ToList();
        foreach (var scope in document.QuerySelectorAll("[itemscope][itemtype]"))
            if (scope.GetAttribute("itemtype")!.Split(' ').Any(type => type.EndsWith("Event", StringComparison.Ordinal)))
                events.Add(ReadScope(scope, 0));
        return events;
    }

    public static string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text : null;

    public static IEnumerable<JsonNode?> Values(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            foreach (var item in array) yield return item;
        }
        else if (value is not null) yield return value;
    }

    private static bool IsEvent(JsonNode? type) => Values(type).Any(value =>
        Text(value)?.Split('/').Last().EndsWith("Event", StringComparison.Ordinal) == true);

    private static void Walk(JsonNode? node, List<JsonObject> records)
    {
        if (node is JsonObject record)
        {
            records.Add(record);
            foreach (var property in record) Walk(property.Value, records);
        }
        else if (node is JsonArray array)
            foreach (var child in array) Walk(child, records);
    }

    private static JsonNode? Resolve(JsonNode? node, Dictionary<string, JsonObject> references, HashSet<string> visited, int depth)
    {
        if (depth > 12) return node?.DeepClone();
        if (node is JsonArray array)
            return new JsonArray(array.Select(child => Resolve(child, references, visited, depth + 1)).ToArray());
        if (node is not JsonObject record) return node?.DeepClone();
        var id = Text(record["@id"]);
        if (id is not null && visited.Contains(id)) return record.DeepClone();
        var next = new HashSet<string>(visited);
        if (id is not null) next.Add(id);
        var merged = id is not null && references.TryGetValue(id, out var reference)
            ? (JsonObject)reference.DeepClone() : new JsonObject();
        foreach (var pair in record) merged[pair.Key] = pair.Value?.DeepClone();
        var result = new JsonObject();
        foreach (var pair in merged) result[pair.Key] = Resolve(pair.Value, references, next, depth + 1);
        return result;
    }

    private static JsonObject ReadScope(IElement scope, int depth)
    {
        var result = new JsonObject { ["@type"] = scope.GetAttribute("itemtype") };
        if (depth > 12) return result;
        foreach (var child in scope.Children) Visit(child);
        return result;

        void Visit(IElement element)
        {
            var properties = element.GetAttribute("itemprop")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var nested = element.HasAttribute("itemscope");
            if (properties.Length > 0)
            {
                JsonNode? value = nested ? ReadScope(element, depth + 1) : JsonValue.Create(
                    element.GetAttribute("content") ?? element.GetAttribute("datetime") ??
                    element.GetAttribute("href") ?? element.GetAttribute("src") ?? element.TextContent.Trim());
                foreach (var property in properties)
                {
                    if (result[property] is null) result[property] = value?.DeepClone();
                    else if (result[property] is JsonArray values) values.Add(value?.DeepClone());
                    else result[property] = new JsonArray(result[property]!.DeepClone(), value?.DeepClone());
                }
            }
            if (!nested) foreach (var child in element.Children) Visit(child);
        }
    }
}
