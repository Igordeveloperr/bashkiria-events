using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Storage;

namespace Bashkiria.Events.Parser.Clients;

public sealed class GkzClient
{
    public static Uri ListingUrl { get; } =
        new("https://gkzbashkortostan.ru/afisha/");

    private readonly HttpClient _httpClient;
    private readonly IRawDocumentStore _documents;

    public GkzClient(HttpClient httpClient, IRawDocumentStore documents)
    {
        _httpClient = httpClient;
        _documents = documents;
    }

    public Task<RawDocument> GetListingPageAsync(
        CancellationToken cancellationToken)
    {
        return DownloadAsync(ListingUrl, cancellationToken);
    }

    public Task<RawDocument> GetEventPageAsync(
        Uri pageUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);

        if (!pageUrl.IsAbsoluteUri ||
            pageUrl.Scheme != Uri.UriSchemeHttps ||
            pageUrl.Host != "gkzbashkortostan.ru" ||
            !pageUrl.IsDefaultPort ||
            !string.IsNullOrEmpty(pageUrl.UserInfo) ||
            !pageUrl.AbsolutePath.StartsWith(
                "/events/",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Ожидается HTTPS-адрес карточки ГКЗ.",
                nameof(pageUrl));
        }

        return DownloadAsync(pageUrl, cancellationToken);
    }

    private async Task<RawDocument> DownloadAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(pageUrl, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = RawDocument.Create(SourceIdentificator.Gkz, pageUrl, (int)response.StatusCode, content);

        // Preserve even 4xx/5xx responses; no parser receives a page before this write succeeds.
        await _documents.SaveAsync(document, cancellationToken);
        response.EnsureSuccessStatusCode();
        return document;
    }
}
