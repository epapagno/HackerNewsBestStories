using HackerNewsBestStories.Api.Models;

namespace HackerNewsBestStories.Api.Services;

public interface IBestStoriesService
{
    Task<IReadOnlyList<StoryResponse>> GetBestStoriesAsync(int count, CancellationToken cancellationToken);
}
