using System.Collections.Concurrent;
using HackerNewsBestStories.Api.Models;
using HackerNewsBestStories.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories.Api.Services;

public sealed class BestStoriesService : IBestStoriesService
{
    private const string CacheKey = "best-stories-ranked";

    private readonly IHackerNewsClient _hackerNewsClient;
    private readonly IMemoryCache _cache;
    private readonly HackerNewsOptions _options;
    private readonly ILogger<BestStoriesService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public BestStoriesService(
        IHackerNewsClient hackerNewsClient,
        IMemoryCache cache,
        IOptions<HackerNewsOptions> options,
        ILogger<BestStoriesService> logger)
    {
        _hackerNewsClient = hackerNewsClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var rankedStories = await GetRankedStoriesAsync(cancellationToken);
        return rankedStories.Take(count).ToArray();
    }

    private async Task<IReadOnlyList<StoryResponse>> GetRankedStoriesAsync(
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<StoryResponse>? cachedStories) &&
            cachedStories is not null)
        {
            return cachedStories;
        }

        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            // Another request may have populated the cache while this one was waiting.
            if (_cache.TryGetValue(CacheKey, out cachedStories) && cachedStories is not null)
            {
                return cachedStories;
            }

            var ids = await _hackerNewsClient.GetBestStoryIdsAsync(cancellationToken);
            var stories = new ConcurrentBag<StoryResponse>();

            await Parallel.ForEachAsync(
                ids,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.MaxConcurrency
                },
                async (id, token) =>
                {
                    try
                    {
                        var item = await _hackerNewsClient.GetStoryAsync(id, token);

                        if (item is null ||
                            item.Deleted is true ||
                            item.Dead is true ||
                            !string.Equals(item.Type, "story", StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(item.Title) ||
                            string.IsNullOrWhiteSpace(item.By))
                        {
                            return;
                        }

                        stories.Add(new StoryResponse(
                            item.Title,
                            item.Url,
                            item.By,
                            DateTimeOffset.FromUnixTimeSeconds(item.Time),
                            item.Score,
                            item.CommentCount));
                    }
                    catch (HttpRequestException exception)
                    {
                        _logger.LogWarning(exception, "Failed to retrieve Hacker News story {StoryId}", id);
                    }
                    catch (TaskCanceledException exception) when (!token.IsCancellationRequested)
                    {
                        _logger.LogWarning(exception, "Timed out retrieving Hacker News story {StoryId}", id);
                    }
                });

            var rankedStories = stories
                .OrderByDescending(story => story.Score)
                .ThenByDescending(story => story.Time)
                .ToArray();

            if (ids.Count > 0 && rankedStories.Length == 0)
            {
                throw new HttpRequestException("No Hacker News story details could be retrieved.");
            }

            _cache.Set(
                CacheKey,
                rankedStories,
                TimeSpan.FromSeconds(_options.CacheSeconds));

            return rankedStories;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
