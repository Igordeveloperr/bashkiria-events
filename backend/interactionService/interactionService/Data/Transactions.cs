using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bashkiria.Events.Interaction.Data;

public sealed class Transactions(IDbContextFactory<InteractionDbContext> contexts)
{
    public async Task<T> ExecuteAsync<T>(Func<InteractionDbContext, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await contexts.CreateDbContextAsync(token);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var result = await operation(db, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (Exception exception) when (attempt < 5 && IsConflict(exception))
            { await Task.Delay(50 * (1 << attempt) + Random.Shared.Next(25, 76), token); }
        }
    }

    private static bool IsConflict(Exception exception) => exception is DbUpdateConcurrencyException ||
        exception is PostgresException { SqlState: "40001" or "40P01" or "23505" } ||
        exception.InnerException is not null && IsConflict(exception.InnerException);
}
