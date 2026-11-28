using HackerNewsBestStories.Api.Models;
using Npgsql;

namespace HackerNewsBestStories.Api.Services;

public sealed class PostgresSnapshotStore(NpgsqlDataSource database, ILogger<PostgresSnapshotStore> logger)
{
    public async Task<StorySnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT fetched_at FROM ranking_metadata WHERE id = 1";
            var timestamp = await command.ExecuteScalarAsync(cancellationToken);
            if (timestamp is not DateTime fetchedAt) return null;
            command.CommandText = """
                SELECT id, title, url, author, published_at, score, comment_count
                FROM stories ORDER BY score DESC, published_at DESC, id ASC LIMIT 500
                """;
            var stories = new List<StoryResponse>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    stories.Add(new StoryResponse(reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                        new DateTimeOffset(reader.GetDateTime(4)), reader.GetInt32(5),
                        reader.GetInt32(6), checked((int)reader.GetInt64(0))));
            }
            await transaction.CommitAsync(cancellationToken);
            return new StorySnapshot(new DateTimeOffset(fetchedAt), stories);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "PostgreSQL snapshot unavailable");
            return null;
        }
    }
}
