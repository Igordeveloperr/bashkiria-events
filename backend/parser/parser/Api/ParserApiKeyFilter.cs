using System.Security.Cryptography;
using System.Text;
using Bashkiria.Events.Parser.Configuration;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Api;

public sealed class ParserApiKeyFilter(IOptions<ParserOptions> options, IHostEnvironment environment) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        if (string.IsNullOrEmpty(expected) && environment.IsDevelopment()) return next(context);
        var provided = context.HttpContext.Request.Headers["X-Parser-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return ValueTask.FromResult<object?>(ApiErrors.Problem(401));
        return next(context);
    }
}
