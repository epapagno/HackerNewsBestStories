using System.Text.Json;
using HackerNewsBestStories.Api.Models;
using StackExchange.Redis;

namespace HackerNewsBestStories.Api.Services;

public sealed record StorySnapshot(DateTimeOffset FetchedAt, IReadOnlyList<StoryResponse> Stories);

public sealed class RedisSnapshotStore(IConnectionMultiplexer connection)
{
    private const string SnapshotKey = "hackernews:{best}:snapshot";
    private const string LeaseKey = "hackernews:{best}:refresh";
    private const string RetryKey = "hackernews:{best}:retry";
    private IDatabase Database => connection.GetDatabase();

    public async Task<StorySnapshot?> ReadAsync()
    {
        var value = await Database.StringGetAsync(SnapshotKey);
        return value.IsNull ? null : JsonSerializer.Deserialize<StorySnapshot>(value.ToString());
    }

    public Task<bool> TryAcquireAsync(string token, TimeSpan duration) =>
        Database.StringSetAsync(LeaseKey, token, duration, When.NotExists);

    public async Task<bool> PublishAsync(string token, StorySnapshot snapshot)
    {
        const string script = """
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            redis.call('SET', KEYS[2], ARGV[2])
            redis.call('DEL', KEYS[3])
            return 1
            """;
        var result = await Database.ScriptEvaluateAsync(script,
            new RedisKey[] { LeaseKey, SnapshotKey, RetryKey },
            new RedisValue[] { token, JsonSerializer.Serialize(snapshot) });
        return (int)result == 1;
    }

    public Task<bool> IsRetryDelayedAsync() => Database.KeyExistsAsync(RetryKey);

    public async Task DelayRetryAsync(string token, TimeSpan duration)
    {
        const string script = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                redis.call('SET', KEYS[2], '1', 'PX', ARGV[2])
            end
            return 0
            """;
        await Database.ScriptEvaluateAsync(script, new RedisKey[] { LeaseKey, RetryKey },
            new RedisValue[] { token, (long)duration.TotalMilliseconds });
    }

    public async Task ReleaseAsync(string token)
    {
        const string script = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                return redis.call('DEL', KEYS[1])
            end
            return 0
            """;
        await Database.ScriptEvaluateAsync(script, new RedisKey[] { LeaseKey },
            new RedisValue[] { token });
    }
}
