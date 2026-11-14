using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Ingestion;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace Bashkiria.Events.Parser.Api;

public static class IngestionEndpoints
{
    public static void MapIngestionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/events", async (HttpRequest request, [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
            EventIngestionService service, CancellationToken token) =>
        {
            if (idempotencyKey == Guid.Empty)
                throw new Common.ParserValidationException("Передайте непустой UUID в заголовке Idempotency-Key.");
            var text = await InputBodyReader.ReadAsync(request, "application/json", IngestionOptions.MaxManualBytes, token);
            var result = await service.CreateAsync(text, idempotencyKey, token);
            var location = $"/api/v1/parser/events/{result.EventId}";
            request.HttpContext.Response.Headers.Location = location;
            return result.Outcome == "added" ? Results.Created(location, result) : Results.Ok(result);
        }).Accepts<EventInput>("application/json")
          .Produces<CreateEventResponse>(201).Produces<CreateEventResponse>(200)
          .ProducesProblem(400).ProducesProblem(409).ProducesProblem(413).ProducesProblem(415);

        api.MapPost("/imports/json/{datasetId}", async (string datasetId, HttpRequest request, EventIngestionService service,
            IOptions<IngestionOptions> options, CancellationToken token) =>
        {
            var text = await InputBodyReader.ReadAsync(request, "application/json", options.Value.MaxFileBytes, token);
            return Results.Ok(await service.ImportAsync(datasetId, text, "application/json", token));
        }).Accepts<EventInput[]>("application/json").Produces<ImportResponse>()
          .ProducesProblem(400).ProducesProblem(413).ProducesProblem(415);

        api.MapPost("/imports/csv/{datasetId}", async (string datasetId, HttpRequest request, EventIngestionService service,
            IOptions<IngestionOptions> options, CancellationToken token) =>
        {
            var text = await InputBodyReader.ReadAsync(request, "text/csv", options.Value.MaxFileBytes, token);
            return Results.Ok(await service.ImportAsync(datasetId, text, "text/csv", token));
        }).Accepts<string>("text/csv").Produces<ImportResponse>()
          .ProducesProblem(400).ProducesProblem(413).ProducesProblem(415);
    }
}
