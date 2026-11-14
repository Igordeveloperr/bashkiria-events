using System.Text.Json;
using Bashkiria.Events.Parser.Models;

namespace Bashkiria.Events.Parser.Contracts;

public sealed record SourceResponse(string Id, string Kind, bool Enabled, string ListingUrl, int Priority);
public sealed record MessageResponse(string Message);
public sealed record EventAuditResponse(Guid Id, string Action, string Actor, DateTimeOffset CreatedAt, JsonElement Snapshot);
public sealed record RawDocumentResponse(Guid Id, string SourceId, Uri Url, DateTimeOffset FetchedAt,
    int HttpStatus, string Content, string ContentHash);
public sealed record EventSaveResponse(Guid EventId, string Outcome, PublicationStatus Status);
public sealed record ReparseResponse(Guid RawDocumentId, IReadOnlyList<EventSaveResponse> Results);
public sealed record StatusCountResponse<T>(T Status, int Count);
public sealed record ParserStatsResponse(int RawDocuments, IReadOnlyList<StatusCountResponse<PublicationStatus>> Events,
    IReadOnlyList<StatusCountResponse<CollectionRunStatus>> Runs);
