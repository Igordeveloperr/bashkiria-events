using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Api;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Parser.Tests;

public sealed class ApiErrorTests
{
    [Theory]
    [InlineData(400, "validation_error")]
    [InlineData(404, "not_found")]
    [InlineData(409, "conflict")]
    [InlineData(500, "internal_error")]
    [InlineData(502, "source_unavailable")]
    [InlineData(503, "service_unavailable")]
    [InlineData(504, "source_timeout")]
    public async Task ExceptionsUseProblemDetailsWithoutExposingInternalMessages(int status, string code)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => ApiErrors.Enrich(context.HttpContext, context.ProblemDetails)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "fixture-trace" };
        context.Request.Path = "/api/v1/parser/events";
        context.Request.Headers.Accept = "text/html";
        context.Response.Body = new MemoryStream();
        Exception exception = status switch
        {
            400 => new BadHttpRequestException("private payload"), 404 => new KeyNotFoundException("private payload"),
            409 => new DbUpdateConcurrencyException("private payload"), 502 => new HttpRequestException("private payload"),
            503 => new ParserStorageException("private payload"), 504 => new TimeoutExceptionWrapper(),
            _ => new InvalidOperationException("private payload")
        };
        Assert.True(await new ParserExceptionHandler(NullLogger<ParserExceptionHandler>.Instance).TryHandleAsync(context, exception, default));
        context.Response.Body.Position = 0;
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("private payload", json);
        Assert.DoesNotContain("stackTrace", json);
        using var document = JsonDocument.Parse(json);
        AssertContract(document.RootElement, status, code, "/api/v1/parser/events");
        Assert.Equal("application/problem+json", context.Response.ContentType?.Split(';')[0]);
    }

    // OperationCanceledException without RequestAborted cancellation is an upstream timeout.
    private sealed class TimeoutExceptionWrapper : OperationCanceledException;

    [PostgresFact]
    public async Task RealApiCardLifecycleAndAllClientErrorsHaveOneContract()
    {
        var connection = Environment.GetEnvironmentVariable("PARSER_TEST_CONNECTION_STRING")!;
        var options = new DbContextOptionsBuilder<ParserDbContext>().UseNpgsql(connection).Options;
        await using var db = new ParserDbContext(options);
        var venueId = await db.Venues.Select(item => item.Id).FirstAsync();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../parser")),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[] { "--urls=http://127.0.0.1:0", "--Parser:WorkerEnabled=false", "--Logging:LogLevel:Default=Warning",
                     "--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information", "--Logging:EventLog:LogLevel:Default=None" }) start.ArgumentList.Add(argument);
        var key = Guid.NewGuid().ToString("N");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__ParserDatabase"] = connection;
        start.Environment["Parser__ApiKey"] = key;
        var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Process { StartInfo = start };
        server.OutputDataReceived += (_, args) =>
        {
            var match = Regex.Match(args.Data ?? "", @"Now listening on: (http://127\.0\.0\.1:\d+)");
            if (match.Success) address.TrySetResult(new Uri(match.Groups[1].Value));
        };
        server.ErrorDataReceived += (_, _) => { };
        try
        {
            Assert.True(server.Start());
            server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using var client = new HttpClient { BaseAddress = await address.Task.WaitAsync(TimeSpan.FromSeconds(30)), Timeout = TimeSpan.FromSeconds(15) };
            await ErrorAsync(await client.GetAsync("/api/v1/parser/events"), 401, "unauthorized");
            client.DefaultRequestHeaders.Add("X-Parser-Key", key);
            await ErrorAsync(await client.GetAsync("/missing-route"), 404, "not_found");
            using var htmlRequest = new HttpRequestMessage(HttpMethod.Get, "/missing-route");
            htmlRequest.Headers.Accept.ParseAdd("text/html");
            await ErrorAsync(await client.SendAsync(htmlRequest), 404, "not_found");
            await ErrorAsync(await client.DeleteAsync("/api/v1/parser/events"), 405, "method_not_allowed");
            await ErrorAsync(await client.GetAsync("/api/v1/parser/events?page=invalid"), 400, "validation_error");
            await ErrorAsync(await client.GetAsync("/api/v1/parser/events?pageSize=101"), 400, "validation_error");
            await ErrorAsync(await client.GetAsync("/api/v1/parser/events/" + Guid.NewGuid()), 404, "not_found");
            await ErrorAsync(await client.PostAsync("/api/v1/parser/events", new StringContent("{}", Encoding.UTF8, "application/json")), 400, "validation_error");
            var idempotencyKey = Guid.NewGuid().ToString();
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            await ErrorAsync(await client.PostAsync("/api/v1/parser/events", new StringContent("{", Encoding.UTF8, "application/json")), 400, "validation_error");
            await ErrorAsync(await client.PostAsync("/api/v1/parser/events", new StringContent("{}", Encoding.UTF8, "text/plain")), 415, "unsupported_media_type");
            await ErrorAsync(await client.PostAsync("/api/v1/parser/events", new StringContent(new string('x', 131073), Encoding.UTF8, "application/json")), 413, "payload_too_large");
            var validation = await ErrorAsync(await client.PostAsJsonAsync("/api/v1/parser/events", new { title = "" }), 400, "validation_error");
            Assert.NotEmpty(validation.GetProperty("errors").EnumerateArray());
            var title = "api-error-fixture-" + Guid.NewGuid();
            var card = new { title, venueId, startDate = "2036-11-15", startTime = "17:00:00", priceMin = 100, ageLimit = 6, sourceStatus = "scheduled" };
            using var created = await client.PostAsJsonAsync("/api/v1/parser/events", card);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
            await ErrorAsync(await client.PostAsJsonAsync("/api/v1/parser/events", new { title = title + "changed" }), 409, "conflict");
            var stored = await client.GetFromJsonAsync<JsonElement>("/api/v1/parser/events/" + id);
            await ErrorAsync(await client.PatchAsJsonAsync($"/api/v1/parser/events/{id}/review", new
            { expectedVersion = -1, status = "published", note = "Stale fixture review" }), 409, "conflict");
            using var reviewed = await client.PatchAsJsonAsync($"/api/v1/parser/events/{id}/review", new
            { expectedVersion = stored.GetProperty("version").GetInt64(), status = "published", note = "Fixture confirmation" });
            Assert.Equal(HttpStatusCode.NoContent, reviewed.StatusCode);
            using var repeated = await client.PostAsJsonAsync("/api/v1/parser/events", card);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            var final = await client.GetFromJsonAsync<JsonElement>("/api/v1/parser/events/" + id);
            Assert.Equal(title, final.GetProperty("title").GetString());
            Assert.Equal("published", final.GetProperty("status").GetString());
            for (var attempt = 0; attempt < 61; attempt++)
            {
                using var response = await client.GetAsync("/api/v1/parser/sources");
                if (response.StatusCode != HttpStatusCode.TooManyRequests) continue;
                Assert.NotNull(response.Headers.RetryAfter);
                await ErrorAsync(response, 429, "rate_limit_exceeded");
                return;
            }
            Assert.Fail("The API rate limit was not enforced.");
        }
        finally
        {
            if (server.Id > 0 && !server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
        }
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, int status, string code)
    {
        using (response)
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            AssertContract(body, status, code, response.RequestMessage!.RequestUri!.AbsolutePath);
            return body;
        }
    }

    private static void AssertContract(JsonElement body, int status, string code, string path)
    {
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal("urn:bashkiria-events:error:" + code, body.GetProperty("type").GetString());
        Assert.Equal(path, body.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Array, body.GetProperty("errors").ValueKind);
    }
}
