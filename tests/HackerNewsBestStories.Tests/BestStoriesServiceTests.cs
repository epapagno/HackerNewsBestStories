using HackerNewsBestStories.Api.Models;
using HackerNewsBestStories.Api.Options;
using HackerNewsBestStories.Api.Services;
using Microsoft.Extensions.Caching.Memory;
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

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);

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

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);

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

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(client, cache);

        var requests = Enumerable.Range(0, 20)
            .Select(_ => service.GetBestStoriesAsync(2, CancellationToken.None));

        await Task.WhenAll(requests);

        Assert.Equal(1, client.BestStoryRequestCount);
        Assert.Equal(3, client.StoryRequestCount);
    }

    private static BestStoriesService CreateService(
        IHackerNewsClient client,
        IMemoryCache cache)
    {
        var options = Options.Create(new HackerNewsOptions
        {
            CacheSeconds = 60,
            MaxConcurrency = 4
        });

        return new BestStoriesService(
            client,
            cache,
            options,
            NullLogger<BestStoriesService>.Instance);
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
