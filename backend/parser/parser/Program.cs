using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Bashkiria.Events.Parser.Api;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Data;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    var envPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".env"));
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.NoClobber().Load(envPath);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
    }
}

builder.Services.AddParser(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options => options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("parser-api", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapOpenApi();
app.MapGet("/health/ready", async (IDbContextFactory<ParserDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    await db.CollectionRuns.AnyAsync(token);
    return Results.Ok(new { status = "ready" });
});
app.MapParserEndpoints();
app.MapPreviewEndpoints();
app.Run();

public partial class Program;
