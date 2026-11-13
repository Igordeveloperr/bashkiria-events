using Npgsql;
using NpgsqlTypes;

namespace Bashkiria.Events.Parser.Storage;

public sealed class PostgresRawDocumentStore : IRawDocumentStore, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;

    public PostgresRawDocumentStore(IConfiguration configuration)
    {
        // Pure HTML previews and /health remain usable without a database connection.
        _dataSource = new Lazy<NpgsqlDataSource>(() =>
        {
            var connectionString = configuration.GetConnectionString("ParserDatabase");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new RawDocumentStorageException(
                    "Configure ConnectionStrings:ParserDatabase before downloading source pages.");

            return NpgsqlDataSource.Create(connectionString);
        });
    }

    public async Task SaveAsync(RawDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _dataSource.Value.CreateCommand("""
                INSERT INTO parser_raw_documents
                    (id, source_id, url, fetched_at, http_status, content, content_hash)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (id) DO NOTHING
                """);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = document.Id });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = document.SourceId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = document.Url.AbsoluteUri });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = document.FetchedAt });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = document.HttpStatus });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = document.Content });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = document.ContentHash });

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RawDocumentStorageException("Could not save the source response.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated)
            await _dataSource.Value.DisposeAsync();
    }
}
