using HackerNewsBestStories.Api.Options;
using HackerNewsBestStories.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();

builder.Services
    .AddOptions<HackerNewsOptions>()
    .Bind(builder.Configuration.GetSection(HackerNewsOptions.SectionName));

builder.Services.AddHttpClient(HackerNewsClient.ClientName, (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

builder.Services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
builder.Services.AddSingleton<IBestStoriesService, BestStoriesService>();

var app = builder.Build();

app.MapControllers();

app.Run();
