using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bashkiria.Events.Interaction.Configuration;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Interaction.Api;

public enum AccessScope { User, Admin, Delivery }
public sealed record ServiceScope(AccessScope Scope);

public sealed class ServiceAccess(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<InteractionOptions> options, IHostEnvironment environment)
    {
        var scope = context.GetEndpoint()?.Metadata.GetMetadata<ServiceScope>()?.Scope;
        if (scope.HasValue)
        {
            context.Response.Headers.CacheControl = "no-store";
            var (expected, header) = scope.Value switch
            {
                AccessScope.Admin => (options.Value.AdminApiKey, "X-Interaction-Admin-Key"),
                AccessScope.Delivery => (options.Value.DeliveryApiKey, "X-Interaction-Delivery-Key"),
                _ => (options.Value.ApiKey, "X-Interaction-Key")
            };
            var provided = context.Request.Headers[header].ToString();
            if (string.IsNullOrEmpty(expected) ? !environment.IsDevelopment() : !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(provided))))
            {
                await ApiErrors.Result(401, title: "Требуется ключ доступа к сервису.").ExecuteAsync(context); return;
            }
            if (scope == AccessScope.User)
            {
                var userId = context.Request.Headers["X-User-Id"].ToString();
                if (!Regex.IsMatch(userId, "^[a-zA-Z0-9_.:-]{1,128}$"))
                { await ApiErrors.Result(400, title: "Передайте идентификатор пользователя в X-User-Id.").ExecuteAsync(context); return; }
                context.Items["interaction-user"] = userId;
            }
        }
        await next(context);
    }
    public static string User(HttpContext context) => (string)context.Items["interaction-user"]!;
}
