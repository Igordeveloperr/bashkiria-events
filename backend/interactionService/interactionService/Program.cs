using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Bashkiria.Events.Interaction.Api;
using Bashkiria.Events.Interaction.Configuration;
using Bashkiria.Events.Interaction.Data;
using Bashkiria.Events.Interaction.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    var env = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".env"));
    if (File.Exists(env)) { DotNetEnv.Env.NoClobber().Load(env); builder.Configuration.AddEnvironmentVariables().AddCommandLine(args); }
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddOptions<InteractionOptions>().BindConfiguration("Interaction").ValidateDataAnnotations()
    .Validate(x => builder.Environment.IsDevelopment() || new[] { x.ApiKey, x.AdminApiKey, x.DeliveryApiKey }.All(key => !string.IsNullOrWhiteSpace(key) && key.Length >= 32),
        "Production requires three API keys of at least 32 characters.")
    .Validate(x => new[] { x.ApiKey, x.AdminApiKey, x.DeliveryApiKey }.Where(key => !string.IsNullOrEmpty(key)).Distinct().Count() ==
        new[] { x.ApiKey, x.AdminApiKey, x.DeliveryApiKey }.Count(key => !string.IsNullOrEmpty(key)), "API keys must be distinct.")
    .ValidateOnStart();
builder.Services.AddDbContextFactory<InteractionDbContext>((provider, options) =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var connection = config.GetConnectionString("InteractionDatabase");
    if (string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(config["PGHOST"]))
        connection = new NpgsqlConnectionStringBuilder
        {
            Host = config["PGHOST"], Port = int.Parse(config["PGPORT"] ?? "5432", System.Globalization.CultureInfo.InvariantCulture),
            Database = config["PGDATABASE"], Username = config["PGUSER"], Password = config["PGPASSWORD"], Timeout = 5, CommandTimeout = 15
        }.ConnectionString;
    if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Configure ConnectionStrings:InteractionDatabase or PGHOST credentials.");
    options.UseNpgsql(connection);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Transactions>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddSingleton<FeedbackService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<ApiMetrics>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.MaxDepth = 32;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddOpenApi(options => options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiErrors.Result(429, title: "Слишком много запросов. Повторите позже.").ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("interaction", context =>
    {
        var identity = context.Items["interaction-user"] as string ?? context.GetEndpoint()?.Metadata.GetMetadata<ServiceScope>()?.Scope.ToString() ?? "other";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    });
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try { await next(context); }
    finally { context.RequestServices.GetRequiredService<ApiMetrics>().Record(context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
});
app.UseExceptionHandler();
app.UseStatusCodePages(context => ApiErrors.Result(context.HttpContext.Response.StatusCode).ExecuteAsync(context.HttpContext));
app.UseRouting();
app.UseMiddleware<ServiceAccess>();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (IDbContextFactory<InteractionDbContext> contexts, CancellationToken ct) =>
{
    await using var db = await contexts.CreateDbContextAsync(ct);
    await db.Subscriptions.AnyAsync(ct); await db.Notifications.AnyAsync(ct); await db.Feedback.AnyAsync(ct);
    await db.Cities.AnyAsync(ct); await db.CatalogEvents.AnyAsync(ct);
    return Results.Ok(new { status = "ready" });
});
app.MapOpenApi();
app.MapGet("/docs", () => Results.Content("""
<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Interaction API</title><body><h1>Подписки, уведомления и обратная связь</h1>
<p><a href="/openapi/v1.json">OpenAPI 3.1 (JSON)</a></p>
<p>Префикс: /api/v1/interaction. Формат JSON: camelCase. Ошибки: application/problem+json.</p>
<p>/me/subscriptions — подписки; /me/notifications — уведомления; /me/feedback — обращения.</p>
<p>/admin/feedback — обработка обращений; /deliveries/claim и /deliveries/{id}/ack — доставка ботом.</p>
<p>Пользовательские запросы: X-Interaction-Key и X-User-Id от доверенного API Gateway.
Администратор: X-Interaction-Admin-Key. Бот: X-Interaction-Delivery-Key.</p>
<p>Полные контракты, параметры и примеры находятся в backend/interactionService/README.md.</p></body></html>
""", "text/html; charset=utf-8"));
app.MapInteractionEndpoints();
app.Run();

public partial class Program;
