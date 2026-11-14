using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Processing;

namespace Bashkiria.Events.Parser.References;

public static class ReferenceKeys
{
    public static string Clean(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    public static string City(string? value) => CandidateNormalizer.NormalizeText(
        Regex.Replace(Clean(value), @"^(?:г\.\s*|г\s+|город\s+)", "", RegexOptions.IgnoreCase));
    public static string Name(string? value) => CandidateNormalizer.NormalizeText(value);
    public static string Address(string? value)
    {
        var text = Clean(value).ToLowerInvariant().Replace('ё', 'е');
        text = Regex.Replace(text, @"\b(?:улица|ул)(?!\p{L})\.?\s*", "ул ");
        text = Regex.Replace(text, @"\b(?:проспект|просп|пр-кт)(?!\p{L})\.?\s*", "проспект ");
        return Name(text);
    }
}
