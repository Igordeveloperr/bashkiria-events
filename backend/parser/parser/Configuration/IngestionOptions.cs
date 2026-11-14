using System.ComponentModel.DataAnnotations;

namespace Bashkiria.Events.Parser.Configuration;

public sealed class IngestionOptions
{
    public const string SectionName = "Parser:Ingestion";
    [Range(1024, 20 * 1024 * 1024)] public int MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    [Range(1, 5000)] public int MaxRows { get; set; } = 1000;
    public const int MaxManualBytes = 128 * 1024;
}
