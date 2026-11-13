using Bashkiria.Events.Parser.Configuration;
using Bashkiria.Events.Parser.Models;
using Bashkiria.Events.Parser.Parsing;

namespace Bashkiria.Events.Parser.Storage;

public interface IEventStore
{
    Task<EventSaveResult> SaveAsync(SourceOptions source, EventCandidate candidate, RawDocument document, CancellationToken cancellationToken);
}

public sealed record EventSaveResult(Guid EventId, string Outcome, PublicationStatus Status);
