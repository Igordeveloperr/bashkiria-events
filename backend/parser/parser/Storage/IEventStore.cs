using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Storage;

public interface IEventStore
{
    Task<EventSaveResult> SaveAsync(EventWriteContext source, EventCandidate candidate, RawDocument document, CancellationToken cancellationToken);
}

public sealed record EventSaveResult(Guid EventId, string Outcome, PublicationStatus Status);

public sealed record EventWriteContext(string Id, int Priority = 0,
    EventSourceType SourceType = EventSourceType.Page, string? Actor = null, bool AllowMergeReplacement = true);
