namespace HackerNewsBestStories.Api.Models;

public enum DataOrigin { Cache, Local, External }

public sealed record BestStoriesResult(
    IReadOnlyList<StoryResponse> Stories,
    DateTimeOffset FetchedAt,
    DataOrigin Origin,
    bool IsStale);
