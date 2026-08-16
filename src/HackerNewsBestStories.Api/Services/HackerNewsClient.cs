using System.Net.Http.Json;
using HackerNewsBestStories.Api.Models;

namespace HackerNewsBestStories.Api.Services;

public sealed class HackerNewsClient : IHackerNewsClient
{
    public const string ClientName = "HackerNews";

    private readonly IHttpClientFactory _httpClientFactory;

    public HackerNewsClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        var ids = await client.GetFromJsonAsync<int[]>("v0/beststories.json", cancellationToken);

        return ids ?? Array.Empty<int>();
    }

    public async Task<HackerNewsItem?> GetStoryAsync(int id, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ClientName);
        return await client.GetFromJsonAsync<HackerNewsItem>($"v0/item/{id}.json", cancellationToken);
    }
}
