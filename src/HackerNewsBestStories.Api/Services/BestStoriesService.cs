using System.Collections.Concurrent;
using HackerNewsBestStories.Api.Models;
using HackerNewsBestStories.Api.Options;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories.Api.Services;

public sealed class BestStoriesService : IBestStoriesService
{
    private readonly IHackerNewsClient _hackerNewsClient;
    private readonly HackerNewsOptions _options;
    private readonly ILogger<BestStoriesService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _stoppingToken;
    private readonly object _gate = new();
    private Snapshot? _snapshot;
    private Task<RefreshResult>? _refreshTask;
    private DateTimeOffset _nextRefreshAttempt;

    private sealed record Snapshot(IReadOnlyList<StoryResponse> Stories, DateTimeOffset RefreshedAt);
    private sealed record RefreshResult(Snapshot? Snapshot, Exception? Error);

    public BestStoriesService(
        IHackerNewsClient hackerNewsClient,
        IOptions<HackerNewsOptions> options,
        ILogger<BestStoriesService> logger,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime)
    {
        _hackerNewsClient = hackerNewsClient;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _stoppingToken = applicationLifetime.ApplicationStopping;
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
        cancellationToken.ThrowIfCancellationRequested();
        Task<RefreshResult> refresh;

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var freshFor = TimeSpan.FromSeconds(_options.CacheSeconds);
            var usableFor = freshFor + TimeSpan.FromSeconds(_options.MaxStaleSeconds);
            var age = _snapshot is null ? TimeSpan.MaxValue : now - _snapshot.RefreshedAt;

            if (_snapshot is not null && age < freshFor)
            {
                return _snapshot.Stories;
            }

            if ((_refreshTask is null || _refreshTask.IsCompleted) && now >= _nextRefreshAttempt)
            {
                // The host owns this work. A caller disconnecting only cancels its own wait.
                _refreshTask = Task.Run(() => RefreshAsync(_stoppingToken), CancellationToken.None);
            }

            if (_snapshot is not null && age < usableFor)
            {
                return _snapshot.Stories;
            }

            if (_refreshTask is null || _refreshTask.IsCompleted && now < _nextRefreshAttempt)
            {
                throw new HttpRequestException("Hacker News refresh is temporarily unavailable; retry later.");
            }

            refresh = _refreshTask;
        }

        // Cold or too-old caches wait for the same refresh, without cancelling it for others.
        var result = await refresh.WaitAsync(cancellationToken);
        if (result.Snapshot is null)
        {
            throw new HttpRequestException("Unable to refresh Hacker News stories.", result.Error);
        }

        return result.Snapshot.Stories;
    }

    private async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
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

            var snapshot = new Snapshot(rankedStories, _timeProvider.GetUtcNow());
            lock (_gate)
            {
                _snapshot = snapshot;
                _nextRefreshAttempt = default;
            }

            return new RefreshResult(snapshot, null);
        }
        catch (Exception exception)
        {
            // Observe background failures and back off even when no usable snapshot exists.
            lock (_gate)
            {
                _nextRefreshAttempt = _timeProvider.GetUtcNow()
                    + TimeSpan.FromSeconds(_options.RefreshRetrySeconds);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception,
                    "Hacker News refresh failed; next attempt in {RetrySeconds} seconds",
                    _options.RefreshRetrySeconds);
            }

            return new RefreshResult(null, exception);
        }
    }
}
