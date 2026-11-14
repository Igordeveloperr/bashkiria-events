using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Parser.Storage;

public sealed class EfRawDocumentStore(IDbContextFactory<ParserDbContext> contexts) : IRawDocumentStore
{
    public async Task SaveAsync(RawDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            if (await db.RawDocuments.AnyAsync(existing => existing.Id == document.Id, cancellationToken))
                return;

            await db.RawDocuments.AddAsync(document, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "parser_raw_documents_pkey" })
            {
                // Retrying the same immutable document is idempotent, including concurrent retries.
            }
        }
        catch (Exception exception) when (DatabaseErrors.IsDatabaseFailure(exception) ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ParserStorageException("Could not save the source response.", exception);
        }
    }
}
