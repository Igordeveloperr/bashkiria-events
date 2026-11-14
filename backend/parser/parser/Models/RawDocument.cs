using System.Security.Cryptography;
using System.Text;

namespace Bashkiria.Events.Parser.Models;

public sealed record RawDocument(
    Guid Id,
    string SourceId,
    Uri Url,
    DateTimeOffset FetchedAt,
    int? HttpStatus,
    string Content,
    string ContentHash,
    EventSourceType SourceType = EventSourceType.Page,
    string ContentType = "text/html")
{
    public static RawDocument Create(string sourceId, Uri url, int httpStatus, string content) =>
        new(Guid.NewGuid(), sourceId, url, DateTimeOffset.UtcNow, httpStatus, content,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
}
