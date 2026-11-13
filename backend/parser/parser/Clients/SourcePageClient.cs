using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Storage;

namespace Bashkiria.Events.Parser.Clients;

public sealed class SourcePageClient(HttpClient httpClient, IRawDocumentStore documents)
{
    public async Task<RawDocument> DownloadAsync(SourceOptions source, Uri pageUrl, CancellationToken cancellationToken)
    {
        if (!SourceUrls.IsAllowed(pageUrl, new Uri(source.ListingUrl)))
            throw new ArgumentException("Адрес находится за пределами разрешённого источника.", nameof(pageUrl));

        for (var redirect = 0; ; redirect++)
        {
            using var response = await httpClient.GetAsync(pageUrl, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = RawDocument.Create(source.Id, SourceUrls.Canonical(pageUrl), (int)response.StatusCode, content);
            await documents.SaveAsync(document, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirect >= 3 || response.Headers.Location is null ||
                    !Uri.TryCreate(pageUrl, response.Headers.Location, out var target) ||
                    !SourceUrls.IsAllowed(target, new Uri(source.ListingUrl)))
                    throw new HttpRequestException("Источник вернул недопустимое перенаправление.", null, response.StatusCode);
                pageUrl = target;
                continue;
            }
            response.EnsureSuccessStatusCode();
            return document;
        }
    }
}
