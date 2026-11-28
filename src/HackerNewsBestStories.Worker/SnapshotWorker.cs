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
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS best_stories_snapshot (
                id integer PRIMARY KEY CHECK (id = 1),
                fetched_at timestamptz NOT NULL,
                persisted_at timestamptz NOT NULL,
                stories jsonb NOT NULL
            );
            INSERT INTO best_stories_snapshot (id, fetched_at, persisted_at, stories)
            VALUES (1, @fetched, now(), @stories)
            ON CONFLICT (id) DO UPDATE SET fetched_at = EXCLUDED.fetched_at,
                persisted_at = EXCLUDED.persisted_at, stories = EXCLUDED.stories
            WHERE best_stories_snapshot.fetched_at < EXCLUDED.fetched_at;
            """;
        command.Parameters.AddWithValue("fetched", NpgsqlDbType.TimestampTz, fetchedAt);
        command.Parameters.AddWithValue("stories", NpgsqlDbType.Jsonb, stories.GetRawText());
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        await args.CompleteMessageAsync(args.Message, token);
    }
}
