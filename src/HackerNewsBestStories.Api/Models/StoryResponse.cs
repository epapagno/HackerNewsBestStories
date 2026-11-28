namespace HackerNewsBestStories.Api.Models;

public sealed class StoryResponse
{
    public StoryResponse(
        string title,
        string? uri,
        string postedBy,
        DateTimeOffset time,
        int score,
        int commentCount,
        int id = 0)
    {
        Id = id;
        Title = title;
        Uri = uri;
        PostedBy = postedBy;
        Time = time;
        Score = score;
        CommentCount = commentCount;
    }

    public int Id { get; }
    public string Title { get; }
    public string? Uri { get; }
    public string PostedBy { get; }
    public DateTimeOffset Time { get; }
    public int Score { get; }
    public int CommentCount { get; }
}
