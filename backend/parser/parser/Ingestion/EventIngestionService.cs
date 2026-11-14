using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Parser.Ingestion;

public sealed partial class EventIngestionService(EventInputReader reader, EventInputMapper mapper,
    CandidateNormalizer normalizer, IRawDocumentStore documents, IEventStore events,
    IDbContextFactory<ParserDbContext> contexts, IOptions<IngestionOptions> options, TimeProvider clock)
{
    public async Task<CreateEventResponse> CreateAsync(string text, Guid key, CancellationToken token)
    {
        if (key == Guid.Empty) throw new ParserValidationException("Idempotency-Key должен содержать непустой UUID.");
        var input = reader.ReadManual(text);
        if (!string.IsNullOrWhiteSpace(input.ExternalId))
            throw new EventInputValidationException([new("externalId", "not_supported", "Для ручного создания используйте Idempotency-Key; externalId относится к импорту.")]);
        var context = new EventWriteContext("manual:api", -10, EventSourceType.Manual, "api-key", AllowMergeReplacement: false);
        var candidate = mapper.Map(input, context.Id, key.ToString("D"));
        var document = MakeDocument("manual:" + key.ToString("D"), context.Id, new Uri("urn:bashkiria-events:manual:" + key.ToString("D")), text, "application/json", context.SourceType);
        var stored = await SaveDocumentAsync(document, token);
        // Compare typed payloads: property order, whitespace and omitted nulls are not a new request.
        if (ParserJson.Serialize(reader.ReadManual(stored.Content)) != ParserJson.Serialize(input))
            throw new IngestionRequestException(409, "Этот Idempotency-Key уже использован для другого мероприятия. Для изменения используйте модерацию с проверкой версии.");
        var saved = await events.SaveAsync(context, candidate, stored, token);
        return new(saved.EventId, saved.Outcome, saved.Status, stored.Id, saved.Issues ?? normalizer.Normalize(context.Id, candidate).Candidate.Issues);
    }

    public async Task<ImportResponse> ImportAsync(string datasetId, string text, string contentType, CancellationToken token)
    {
        ValidateDataset(datasetId);
        var rows = ReadRows(text, contentType);
        var sourceId = "import:" + datasetId;
        var hash = CandidateNormalizer.Hash(text);
        var document = MakeDocument("import:" + datasetId + ":" + contentType + ":" + hash, sourceId,
            new Uri("urn:bashkiria-events:import:" + datasetId + ":" + hash), text, contentType, EventSourceType.Import);
        var stored = await SaveDocumentAsync(document, token);
        return await SaveRowsAsync(datasetId, rows, stored, token);
    }

    public async Task<ImportResponse> ReplayAsync(RawDocument document, CancellationToken token)
    {
        if (document.SourceType == EventSourceType.Import && document.SourceId.StartsWith("import:", StringComparison.Ordinal))
            return await SaveRowsAsync(document.SourceId[7..], ReadRows(document.Content, document.ContentType), document, token);
        if (document.SourceType != EventSourceType.Manual || !Guid.TryParse(document.Url.AbsoluteUri.Split(':').Last(), out var key))
            throw new ParserValidationException("Неизвестный формат исходного документа.");
        var context = new EventWriteContext(document.SourceId, -10, EventSourceType.Manual, "api-key", AllowMergeReplacement: false);
        var input = reader.ReadManual(document.Content);
        var candidate = mapper.Map(input, context.Id, key.ToString("D"));
        var saved = await events.SaveAsync(context, candidate, document, token);
        return new(document.Id, null, [new(1, candidate.ExternalId, saved.EventId, saved.Outcome, saved.Status,
            saved.Issues ?? normalizer.Normalize(context.Id, candidate).Candidate.Issues, [])]);
    }

    private async Task<ImportResponse> SaveRowsAsync(string datasetId, IReadOnlyList<InputRow> rows, RawDocument document, CancellationToken token)
    {
        var context = new EventWriteContext(document.SourceId, -20, EventSourceType.Import, "api-key", AllowMergeReplacement: false);
        var result = new List<ImportRowResponse>(rows.Count);
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Value is null)
            {
                result.Add(new(row.Row, null, null, "rejected", null, [], row.Errors));
                continue;
            }
            var errors = mapper.Validate(row.Value).ToList();
            var externalId = row.Value.ExternalId?.Trim();
            if (!string.IsNullOrEmpty(externalId) && !externalIds.Add(externalId))
                errors.Add(new("externalId", "duplicate_external_id", "externalId повторяется в этом файле. Каждому сеансу нужен отдельный идентификатор."));
            if (errors.Count > 0)
            {
                result.Add(new(row.Row, externalId, null, "rejected", null, [], errors));
                continue;
            }
            var candidate = mapper.Map(row.Value, context.Id);
            try
            {
                var saved = await events.SaveAsync(context, candidate, document, token);
                result.Add(new(row.Row, candidate.ExternalId, saved.EventId, saved.Outcome, saved.Status,
                    saved.Issues ?? normalizer.Normalize(context.Id, candidate).Candidate.Issues, []));
            }
            catch (ParserValidationException exception)
            {
                result.Add(new(row.Row, candidate.ExternalId, null, "rejected", null, [], [new("reference", "invalid_reference", exception.Message)]));
            }
        }
        return new(document.Id, datasetId, result);
    }

    private IReadOnlyList<InputRow> ReadRows(string text, string contentType) => contentType switch
    {
        "application/json" => reader.ReadJson(text, options.Value.MaxRows),
        "text/csv" => reader.ReadCsv(text, options.Value.MaxRows),
        _ => throw new IngestionRequestException(415, "Поддерживаются application/json и text/csv.")
    };

    private RawDocument MakeDocument(string identity, string sourceId, Uri url, string content, string contentType, EventSourceType type) =>
        new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16)), sourceId, url, clock.GetUtcNow(), null,
            content, CandidateNormalizer.Hash(content), type, contentType);

    private async Task<RawDocument> SaveDocumentAsync(RawDocument document, CancellationToken token)
    {
        await documents.SaveAsync(document, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.RawDocuments.AsNoTracking().SingleAsync(item => item.Id == document.Id, token);
    }

    private static void ValidateDataset(string id)
    {
        if (!DatasetId().IsMatch(id)) throw new ParserValidationException("datasetId: 1–64 символа, строчная латиница, цифры, _ и -; первый символ — буква.");
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")]
    private static partial Regex DatasetId();
}
