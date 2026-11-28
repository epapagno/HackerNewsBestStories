using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace HackerNewsBestStories.Api.Services;

public sealed class SnapshotPublisher(ServiceBusClient client, ILogger<SnapshotPublisher> logger) : IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender("story-snapshots");

    public async Task PublishAsync(StorySnapshot snapshot, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var message = new ServiceBusMessage(JsonSerializer.Serialize(snapshot))
            {
                ContentType = "application/json",
                MessageId = Guid.NewGuid().ToString("N")
            };
            await _sender.SendMessageAsync(message, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Snapshot {FetchedAt} could not be published", snapshot.FetchedAt);
        }
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
