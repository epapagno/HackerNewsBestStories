using System.Text.Json.Serialization;

namespace HackerNewsBestStories.Api.Models;

public sealed class HackerNewsItem
{
    public int Id { get; init; }
    public string? By { get; init; }
    public long Time { get; init; }
    public string? Title { get; init; }
    public string? Url { get; init; }
    public int Score { get; init; }
    public string? Type { get; init; }
    public bool? Deleted { get; init; }
    public bool? Dead { get; init; }

    [JsonPropertyName("descendants")]
    public int CommentCount { get; init; }
}
