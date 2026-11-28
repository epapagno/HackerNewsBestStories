using Azure.Messaging.ServiceBus;
using HackerNewsBestStories.Api.Options;
using HackerNewsBestStories.Api.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Services
    .AddOptions<HackerNewsOptions>()
    .Bind(builder.Configuration.GetSection(HackerNewsOptions.SectionName))
    .Validate(options => options.CacheSeconds > 0, "CacheSeconds must be positive.")
    .Validate(options => options.MaxStaleSeconds >= 0, "MaxStaleSeconds must not be negative.")
    .Validate(options => options.RefreshRetrySeconds > 0, "RefreshRetrySeconds must be positive.")
    .Validate(options => options.MaxConcurrency > 0, "MaxConcurrency must be positive.")
    .Validate(options => options.TimeoutSeconds > 0, "TimeoutSeconds must be positive.")
    .Validate(options => options.MaxRequestedStories > 0, "MaxRequestedStories must be positive.")
    .Validate(options => options.RefreshRetryWithDataSeconds > 0 && options.RefreshLeaseSeconds > 0,
        "Distributed refresh intervals must be positive.")
    .ValidateOnStart();

builder.Services.AddHttpClient(HackerNewsClient.ClientName, (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"]
        ?? "localhost:6379,abortConnect=false"));
builder.Services.AddSingleton<RedisSnapshotStore>();
var busConnection = builder.Configuration.GetConnectionString("ServiceBus");
if (!string.IsNullOrWhiteSpace(busConnection))
{
    builder.Services.AddSingleton(_ => new ServiceBusClient(busConnection));
    builder.Services.AddSingleton<SnapshotPublisher>();
}

builder.Services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
builder.Services.AddSingleton<IBestStoriesService, BestStoriesService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapControllers();

app.Run();
