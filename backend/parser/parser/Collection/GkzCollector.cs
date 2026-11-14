namespace Bashkiria.Events.Parser.Collection;

// Compatibility preview; production collection uses durable jobs.
public sealed class GkzCollector(SourceCollector collector)
{
    public Task<CollectionResult> CollectAsync(CancellationToken cancellationToken) =>
        collector.CollectAsync("gkz", 3, null, cancellationToken);
}
