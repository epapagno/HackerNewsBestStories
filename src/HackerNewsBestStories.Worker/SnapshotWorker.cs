using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Npgsql;
using NpgsqlTypes;

namespace HackerNewsBestStories.Worker;

public sealed class SnapshotWorker(ServiceBusClient bus, NpgsqlDataSource database,
    ILogger<SnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = bus.CreateProcessor("story-snapshots",
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        processor.ProcessMessageAsync += PersistAsync;
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus processing error");
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await processor.StopProcessingAsync(CancellationToken.None); }
    }

    private async Task PersistAsync(ProcessMessageEventArgs args)
    {
        var token = args.CancellationToken;
        using var document = JsonDocument.Parse(args.Message.Body.ToMemory());
        var root = document.RootElement;
        var fetchedAt = root.GetProperty("FetchedAt").GetDateTimeOffset().ToUniversalTime();
        var stories = root.GetProperty("Stories");
        if (stories.ValueKind != JsonValueKind.Array || stories.GetArrayLength() > 500)
            throw new InvalidDataException("Invalid snapshot size");
        var ids = new HashSet<long>();
        foreach (var story in stories.EnumerateArray())
        {
            var id = story.GetProperty("Id").GetInt64();
            if (id <= 0 || !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate story identifier");
        }

        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_advisory_xact_lock(73102841);
            CREATE TABLE IF NOT EXISTS stories (
                id bigint PRIMARY KEY,
                title text NOT NULL,
                url text,
                score integer NOT NULL,
                author text NOT NULL,
                published_at timestamptz NOT NULL,
                comment_count integer NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS ranking_metadata (
                id integer PRIMARY KEY CHECK (id = 1),
                fetched_at timestamptz NOT NULL,
                persisted_at timestamptz NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = """
            INSERT INTO ranking_metadata (id, fetched_at, persisted_at)
            VALUES (1, @fetched, now())
            ON CONFLICT (id) DO UPDATE SET fetched_at = EXCLUDED.fetched_at,
                persisted_at = EXCLUDED.persisted_at
            WHERE ranking_metadata.fetched_at < EXCLUDED.fetched_at
            RETURNING id;
            """;
        command.Parameters.AddWithValue("fetched", NpgsqlDbType.TimestampTz, fetchedAt);
        var accepted = await command.ExecuteScalarAsync(token);
        if (accepted is not null)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO stories (id, title, url, score, author, published_at, comment_count, updated_at)
                SELECT x."Id", x."Title", x."Uri", x."Score", x."PostedBy", x."Time", x."CommentCount", now()
                FROM jsonb_to_recordset(@payload) AS x(
                    "Id" bigint, "Title" text, "Uri" text, "Score" integer,
                    "PostedBy" text, "Time" timestamptz, "CommentCount" integer)
                ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, url = EXCLUDED.url,
                    score = EXCLUDED.score, author = EXCLUDED.author,
                    published_at = EXCLUDED.published_at, comment_count = EXCLUDED.comment_count,
                    updated_at = EXCLUDED.updated_at;
                DELETE FROM stories WHERE NOT (id = ANY(@ids));
                """;
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, stories.GetRawText());
            command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids.ToArray());
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        await args.CompleteMessageAsync(args.Message, token);
    }
}
