using HackerNewsBestStories.Api.Models;

namespace HackerNewsBestStories.Api.Services;

public interface IBestStoriesService
{
    Task<BestStoriesResult> GetBestStoriesResultAsync(int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(int count, CancellationToken cancellationToken);
}
