using HackerNewsBestStories.Api.Models;
using HackerNewsBestStories.Api.Options;
using HackerNewsBestStories.Api.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HackerNewsBestStories.Tests;

public sealed class BestStoriesServiceTests
{
    [Fact]
    public async Task GetBestStoriesAsync_OrdersByScoreAndTakesRequestedCount()
    {
        var client = new FakeHackerNewsClient(
            new[] { 1, 2, 3 },
            new Dictionary<int, HackerNewsItem>
            {
                [1] = Story(1, "low", 10),
                [2] = Story(2, "high", 100),
                [3] = Story(3, "middle", 50)
            });

        var service = CreateService(client);

        var result = await service.GetBestStoriesAsync(2, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { 100, 50 }, result.Select(story => story.Score));
        Assert.Equal(new[] { "high", "middle" }, result.Select(story => story.Title));
    }

    [Fact]
    public async Task GetBestStoriesAsync_ReusesRankedCacheForDifferentCounts()
    {
        var client = new FakeHackerNewsClient(
            new[] { 1, 2, 3 },
            new Dictionary<int, HackerNewsItem>
            {
                [1] = Story(1, "one", 30),
                [2] = Story(2, "two", 20),
                [3] = Story(3, "three", 10)
            });

        var service = CreateService(client);

        await service.GetBestStoriesAsync(1, CancellationToken.None);
        await service.GetBestStoriesAsync(3, CancellationToken.None);

        Assert.Equal(1, client.BestStoryRequestCount);
        Assert.Equal(3, client.StoryRequestCount);
    }

    [Fact]
    public async Task GetBestStoriesAsync_OnlyPerformsOneRefreshForConcurrentRequests()
    {
        var client = new FakeHackerNewsClient(
            new[] { 1, 2, 3 },
            new Dictionary<int, HackerNewsItem>
            {
                [1] = Story(1, "one", 30),
                [2] = Story(2, "two", 20),
                [3] = Story(3, "three", 10)
            },
            delayMilliseconds: 25);

        var service = CreateService(client);

        var requests = Enumerable.Range(0, 500)
            .Select(_ => service.GetBestStoriesAsync(2, CancellationToken.None));

        await Task.WhenAll(requests);

        Assert.Equal(1, client.BestStoryRequestCount);
        Assert.Equal(3, client.StoryRequestCount);
    }

    [Fact]
    public async Task StaleCache_Serves500CallersWithoutWaiting_AndPublishesOneRefresh()
    {
        var clock = new ManualClock();
        var started = Signal();
        var release = Signal();
        var client = new ControlledClient();
        var service = CreateService(client, clock);
        await service.GetBestStoriesAsync(1, CancellationToken.None);

        clock.Advance(60);
        client.GetIds = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new[] { 1 };
        };
        client.Score = 200;

        var requests = Enumerable.Range(0, 500)
            .Select(_ => service.GetBestStoriesAsync(1, CancellationToken.None)).ToArray();
        var stale = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(stale, stories => Assert.Equal(100, Assert.Single(stories).Score));
        Assert.Equal(2, client.IdRequests);
        Assert.False(release.Task.IsCompleted);

        // At the hard expiry, a caller must wait for the existing refresh.
        clock.Advance(300);
        var waiting = service.GetBestStoriesAsync(1, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        var fresh = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, Assert.Single(fresh).Score);
        Assert.Equal(2, client.IdRequests);
        Assert.Equal(2, client.StoryRequests);
    }

    [Fact]
    public async Task ColdCache_FailuresBackOffAcross500Requests_AndRecoverAfterCooldown()
    {
        var clock = new ManualClock();
        var client = new ControlledClient
        {
            GetIds = _ => throw new HttpRequestException("upstream unavailable")
        };
        var service = CreateService(client, clock);

        var requests = Enumerable.Range(0, 500).Select(_ =>
            Assert.ThrowsAsync<HttpRequestException>(() =>
                service.GetBestStoriesAsync(1, CancellationToken.None)));
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.IdRequests);

        client.GetIds = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 1 });
        clock.Advance(14);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetBestStoriesAsync(1, CancellationToken.None));
        Assert.Equal(1, client.IdRequests);

        clock.Advance(1);
        var recovered = await service.GetBestStoriesAsync(1, CancellationToken.None);
        Assert.Equal(100, Assert.Single(recovered).Score);
        Assert.Equal(2, client.IdRequests);
    }

    [Fact]
    public async Task FailedRefresh_KeepsLastSnapshot_ButNeverServesItBeyondHardExpiry()
    {
        var clock = new ManualClock();
        var started = Signal();
        var release = Signal();
        var client = new ControlledClient();
        var service = CreateService(client, clock);
        await service.GetBestStoriesAsync(1, CancellationToken.None);

        client.GetIds = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new HttpRequestException("upstream unavailable");
        };
        clock.Advance(60);
        var stale = await service.GetBestStoriesAsync(1, CancellationToken.None);
        Assert.Equal(100, Assert.Single(stale).Score);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(300);
        var expired = service.GetBestStoriesAsync(1, CancellationToken.None);
        Assert.False(expired.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => expired);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetBestStoriesAsync(1, CancellationToken.None));
        Assert.Equal(2, client.IdRequests);
    }

    [Fact]
    public async Task CancellingCaller_DoesNotCancelSharedColdRefresh()
    {
        var started = Signal();
        var release = Signal();
        var client = new ControlledClient
        {
            GetIds = async token =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return new[] { 1 };
            }
        };
        var service = CreateService(client);
        using var cancellation = new CancellationTokenSource();
        var first = service.GetBestStoriesAsync(1, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.GetBestStoriesAsync(1, CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);

        release.SetResult();
        Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.IdRequests);
    }

    [Fact]
    public async Task ApplicationShutdown_CancelsSharedRefresh()
    {
        var started = Signal();
        var cancelled = Signal();
        var lifetime = new TestLifetime();
        var client = new ControlledClient
        {
            GetIds = async token =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Array.Empty<int>();
                }
                finally
                {
                    if (token.IsCancellationRequested) cancelled.TrySetResult();
                }
            }
        };
        var service = CreateService(client, lifetime: lifetime);
        var request = service.GetBestStoriesAsync(1, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => request);
    }

    [Fact]
    public async Task Refresh_NeverExceedsConfiguredUpstreamConcurrency()
    {
        var saturated = Signal();
        var release = Signal();
        var active = 0;
        var peak = 0;
        var client = new ControlledClient
        {
            GetIds = _ => Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, 50).ToArray()),
            GetStory = async (id, token) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (release)
                {
                    peak = Math.Max(peak, current);
                    if (current == 4) saturated.TrySetResult();
                }
                try
                {
                    await release.Task.WaitAsync(token);
                    return Story(id, "story", id);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        };
        var service = CreateService(client);
        var request = service.GetBestStoriesAsync(50, CancellationToken.None);
        await saturated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal(50, (await request.WaitAsync(TimeSpan.FromSeconds(5))).Count);
        Assert.Equal(4, peak);
        Assert.Equal(50, client.StoryRequests);
    }

    [Fact]
    public async Task FailedBackgroundRefresh_ContinuesServingSnapshotDuringCooldown()
    {
        var clock = new ManualClock();
        var failureLogged = Signal();
        var client = new ControlledClient();
        var options = Options.Create(new HackerNewsOptions { CacheSeconds = 60 });
        var service = new BestStoriesService(client, options,
            new FailureLogger(failureLogged), clock, new TestLifetime());
        await service.GetBestStoriesAsync(1, CancellationToken.None);
        client.GetIds = _ => throw new HttpRequestException("upstream unavailable");
        clock.Advance(60);
        await service.GetBestStoriesAsync(1, CancellationToken.None);
        await failureLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var requests = Enumerable.Range(0, 500)
            .Select(_ => service.GetBestStoriesAsync(1, CancellationToken.None));
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, stories => Assert.Equal(100, Assert.Single(stories).Score));
        Assert.Equal(2, client.IdRequests);
    }

    private sealed class FailureLogger(TaskCompletionSource logged)
        : Microsoft.Extensions.Logging.ILogger<BestStoriesService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) logged.TrySetResult();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.Parse("2026-01-01T00:00:00Z").Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class ControlledClient : IHackerNewsClient
    {
        private int _idRequests;
        private int _storyRequests;
        public int IdRequests => Volatile.Read(ref _idRequests);
        public int StoryRequests => Volatile.Read(ref _storyRequests);
        public int Score { get; set; } = 100;
        public Func<CancellationToken, Task<IReadOnlyList<int>>> GetIds { get; set; }
            = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 1 });

        public Func<int, CancellationToken, Task<HackerNewsItem?>>? GetStory { get; set; }

        public Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _idRequests);
            return GetIds(cancellationToken);
        }

        public Task<HackerNewsItem?> GetStoryAsync(int id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _storyRequests);
            return GetStory is null
                ? Task.FromResult<HackerNewsItem?>(Story(id, "story", Score))
                : GetStory(id, cancellationToken);
        }
    }

    private static BestStoriesService CreateService(
        IHackerNewsClient client,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? lifetime = null)
    {
        var options = Options.Create(new HackerNewsOptions
        {
            CacheSeconds = 60,
            MaxConcurrency = 4
        });

        return new BestStoriesService(
            client,
            options,
            NullLogger<BestStoriesService>.Instance,
            timeProvider ?? TimeProvider.System,
            lifetime ?? new TestLifetime());
    }

    private static HackerNewsItem Story(int id, string title, int score)
    {
        return new HackerNewsItem
        {
            Id = id,
            By = "tester",
            Time = 1_700_000_000 + id,
            Title = title,
            Url = $"https://example.com/{id}",
            Score = score,
            Type = "story",
            CommentCount = id
        };
    }

    private sealed class FakeHackerNewsClient : IHackerNewsClient
    {
        private readonly IReadOnlyList<int> _ids;
        private readonly IReadOnlyDictionary<int, HackerNewsItem> _stories;
        private readonly int _delayMilliseconds;
        private int _bestStoryRequestCount;
        private int _storyRequestCount;

        public FakeHackerNewsClient(
            IReadOnlyList<int> ids,
            IReadOnlyDictionary<int, HackerNewsItem> stories,
            int delayMilliseconds = 0)
        {
            _ids = ids;
            _stories = stories;
            _delayMilliseconds = delayMilliseconds;
        }

        public int BestStoryRequestCount => _bestStoryRequestCount;
        public int StoryRequestCount => _storyRequestCount;

        public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bestStoryRequestCount);
            await Delay(cancellationToken);
            return _ids;
        }

        public async Task<HackerNewsItem?> GetStoryAsync(int id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _storyRequestCount);
            await Delay(cancellationToken);

            _stories.TryGetValue(id, out var story);
            return story;
        }

        private Task Delay(CancellationToken cancellationToken)
        {
            return _delayMilliseconds > 0
                ? Task.Delay(_delayMilliseconds, cancellationToken)
                : Task.CompletedTask;
        }
    }
}
