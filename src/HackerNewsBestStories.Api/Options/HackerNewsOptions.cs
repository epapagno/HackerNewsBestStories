namespace HackerNewsBestStories.Api.Options;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    public string BaseUrl { get; init; } = "https://hacker-news.firebaseio.com/";
    public int CacheSeconds { get; init; } = 30;
    public int MaxStaleSeconds { get; init; } = 300;
    public int RefreshRetrySeconds { get; init; } = 15;
    public int MaxConcurrency { get; init; } = 20;
    public int MaxRequestedStories { get; init; } = 500;
    public int TimeoutSeconds { get; init; } = 10;
}
