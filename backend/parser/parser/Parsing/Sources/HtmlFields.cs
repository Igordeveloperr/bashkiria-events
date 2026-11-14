using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Bashkiria.Events.Parser.Parsing.Sources;

internal static partial class HtmlFields
{
    public static string Text(IElement? element)
    {
        if (element is null) return string.Empty;
        var copy = (IElement)element.Clone(true);
        foreach (var script in copy.QuerySelectorAll("script, style")) script.Remove();
        return Clean(copy.TextContent);
    }
    public static string Clean(string? value) => Whitespace().Replace(value ?? string.Empty, " ").Trim();
    public static Uri? Link(string? value, Uri page) => !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(page, value, out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo) ? url : null;
    public static TimeOnly? Time(string? value) => TimeOnly.TryParseExact(value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ? time : null;
    public static DateTime? Timestamp(string? value) => DateTime.TryParseExact(value,
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"],
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ? timestamp : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
