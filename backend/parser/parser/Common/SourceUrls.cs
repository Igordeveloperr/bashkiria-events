namespace Bashkiria.Events.Parser.Common;

public static class SourceUrls
{
    public static string Host(Uri uri) => uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        ? uri.IdnHost[4..].ToLowerInvariant() : uri.IdnHost.ToLowerInvariant();

    public static bool IsAllowed(Uri uri, Uri listingUrl) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && Host(uri) == Host(listingUrl);

    public static Uri Canonical(Uri uri)
    {
        if (uri.Scheme == "urn") return uri;
        var builder = new UriBuilder(uri) { Fragment = string.Empty, Port = uri.IsDefaultPort ? -1 : uri.Port };
        // Event identities must not depend on marketing tags; other query parameters can be meaningful.
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)
            .Where(pair => !pair.Key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        builder.Query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(string.Empty, query).TrimStart('?');
        return builder.Uri;
    }
}
