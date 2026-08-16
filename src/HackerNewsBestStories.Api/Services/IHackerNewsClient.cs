using HackerNewsBestStories.Api.Models;

namespace HackerNewsBestStories.Api.Services;

public interface IHackerNewsClient
{
    Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken);
    Task<HackerNewsItem?> GetStoryAsync(int id, CancellationToken cancellationToken);
}
