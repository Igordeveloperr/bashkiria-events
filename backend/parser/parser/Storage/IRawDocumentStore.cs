using Bashkiria.Events.Parser.Models;
namespace Bashkiria.Events.Parser.Storage;

public interface IRawDocumentStore
{
    Task SaveAsync(RawDocument document, CancellationToken cancellationToken);
}
