namespace Bashkiria.Events.Parser.Clients;

public sealed class GkzClient
{
    public static Uri ListingUrl { get; } =
        new("https://gkzbashkortostan.ru/afisha/");

    private readonly HttpClient _httpClient;

    public GkzClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetListingHtmlAsync(
        CancellationToken cancellationToken)
    {
        return await _httpClient.GetStringAsync(
            ListingUrl,
            cancellationToken);
    }

    public async Task<string> GetEventHtmlAsync(
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

        return await _httpClient.GetStringAsync(
            pageUrl,
            cancellationToken);
    }
}
