using Azure.Messaging.ServiceBus;
using Npgsql;
using HackerNewsBestStories.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(_ => new ServiceBusClient(
    builder.Configuration.GetConnectionString("ServiceBus")
    ?? throw new InvalidOperationException("ServiceBus connection required")));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection required")));
builder.Services.AddHostedService<SnapshotWorker>();
await builder.Build().RunAsync();
