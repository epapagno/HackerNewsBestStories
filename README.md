# Hacker News Best Stories API

Small ASP.NET Core API that returns the best `n` Hacker News stories ordered by score.

## Requirements

- .NET 8 SDK

## Run

From the repository root:

```bash
dotnet restore
dotnet run --project src/HackerNewsBestStories.Api
```

The exact port can vary depending on the local ASP.NET Core configuration. Use the URL printed in the console when the application starts.

You can call the API directly:

```bash
curl "http://localhost:5000/api/stories/best?qtyStories=10"
```

## Swagger

Swagger UI is available when running the application in the Development environment.

After starting the API, open:

```text
http://localhost:<port>/swagger
```

From Swagger UI, expand `GET /api/stories/best`, click **Try it out**, provide a value for `qtyStories` (for example `10`), and execute the request.

Swagger is included to make the API contract easy to inspect and to provide a simple way to test the endpoint without needing an external HTTP client.

## Test

```bash
dotnet test
```

## Endpoint

`GET /api/stories/best?qtyStories={count}`

Example response:

```json
[
  {
    "title": "Example story",
    "uri": "https://example.com/story",
    "postedBy": "user",
    "time": "2026-08-16T10:00:00+00:00",
    "score": 123,
    "commentCount": 42
  }
]
```

`qtyStories` must be between 1 and 500. If Hacker News exposes fewer than `qtyStories` best stories at that moment, the API returns the stories that are available.

## Approach

The Hacker News `beststories` endpoint returns story IDs. I do not assume those IDs are strictly ordered by score, because the API documentation does not make that guarantee. The application therefore:

1. Retrieves the current best-story IDs.
2. Retrieves the details for those stories with bounded parallelism.
3. Sorts the valid stories by `score` descending.
4. Caches that ranked list in memory.
5. Returns the first `n` entries from the cached list.

The same ranked cache is shared for every value of `n`, so a request for `qtyStories=10` followed by `qtyStories=20` does not trigger another Hacker News refresh while the cache is valid.

## Protecting the Hacker News API

The cache uses **stale-while-revalidate**: callers can continue reading the last
successful ranked snapshot while a single refresh runs in the background. The
response format and score ordering remain unchanged.

| Snapshot state | Request behavior |
| --- | --- |
| Fresh (under 60 seconds by default) | Return immediately without contacting Hacker News. |
| Stale but usable (60 to under 360 seconds) | Return immediately and start or join one background refresh. |
| Missing or too old (360 seconds or older) | Wait for the shared refresh; return HTTP 503 if it fails or is in cooldown. |
| Refresh failed | Keep the last successful snapshot and wait 15 seconds before allowing another attempt. Serve it only while it remains usable. |

The stale allowance is **additional** to the fresh duration. Freshness starts
when a refresh completes successfully, not when a caller reads the data. A failed
refresh never extends that timestamp. Refreshes are triggered by demand; there
is no periodic poller making requests when there is no traffic.

The refresh belongs to the application lifetime. A caller disconnecting cancels
only its own wait; it cannot abort the work shared by other callers. Application
shutdown cancels upstream work. Background failures are observed and logged.

Configuration is in `appsettings.json` (or standard ASP.NET Core environment overrides):

| Setting | Default | Purpose |
| --- | --- | --- |
| `HackerNews:CacheSeconds` | 60 | Fresh snapshot lifetime. Must be positive. |
| `HackerNews:MaxStaleSeconds` | 300 | Additional stale allowance. Set to 0 to require fresh data. |
| `HackerNews:RefreshRetrySeconds` | 15 | Minimum pause after a failed refresh. Must be positive. |
| `HackerNews:MaxConcurrency` | 20 | Maximum simultaneous detail requests within one refresh. |
| `HackerNews:TimeoutSeconds` | 10 | Timeout of each upstream HTTP request. |

For a refresh with `m` story IDs, the application makes one IDs request plus at
most `m` detail requests, regardless of how many callers share that refresh.
It does not automatically retry failed HTTP requests. Detail failures are skipped
as before; if all details fail for a nonempty IDs list, the refresh fails and
preserves the previous snapshot. A partially successful refresh can publish a
smaller result set.

The single-refresh guarantee, concurrency bound and cooldown apply **per process**.
Multiple replicas each maintain their own cache and request budget; a distributed
cache with coordinated refresh ownership would be needed to share these limits.
Bounded concurrency is not a strict upstream requests-per-second limit.

### Verifying behavior under concurrency and failure

Run `dotnet test`. The suite includes 500 concurrent cold-cache callers sharing
one refresh, 500 stale-cache callers completing while a simulated upstream is
blocked, and 500 callers during an outage without repeated refresh attempts.
Other checks cover hard expiry, recovery after cooldown, cancellation isolation,
application shutdown and the configured detail concurrency bound.

Tests use a controlled upstream and a manually advanced clock, so no load is sent
to the public Hacker News API. These verify concurrency contracts; they are not
production throughput or latency benchmarks.

## Assumptions

- `uri` maps directly to Hacker News' optional `url` field, so it can be `null` for stories such as Ask HN posts that do not have an external URL.
- `commentCount` maps to the Hacker News `descendants` field.
- Deleted, dead, null, or malformed story records are skipped.
- When two stories have the same score, newer stories are returned first to keep ordering deterministic.
- A maximum caller value of 500 is used as a defensive API limit. If fewer best-story IDs are available, the API returns the available set.

## What I would improve with more time

- Add integration tests around the HTTP endpoint and error responses.
- Add an explicit upstream requests-per-second budget if a provider quota requires it; the existing concurrency limit and failed-refresh cooldown serve different purposes.
- Use a distributed cache such as Redis if the API needed to run on multiple instances, so every instance does not refresh Hacker News independently.
- Coordinate those refreshes across instances with a message bus (MassTransit over RabbitMQ) if the system grew event-driven: a single worker would own the refresh and publish a `BestStoriesRefreshed` event that each instance consumes to update its local cache, extending the per-process single-refresh guarantee to the whole cluster instead of every replica polling Hacker News on its own. This stays off the synchronous read path, which remains a direct in-process call so request latency is not coupled to a broker.
- Expose cache age and refresh metrics so freshness and upstream protection can be monitored in production.
- Measure throughput and latency with an HTTP load test against a simulated upstream before tuning cache duration or concurrency.
- Add container/deployment configuration if the target environment required it.


## Distributed deployment (local development)

The API serves the ranking from Cache, Local persistence or External provider.
Successful refreshes publish the full ranking to Service Bus; the worker commits
the relational stories and ranking_metadata tables before acknowledging messages.

### Docker Compose

Copy .env.example to .env, supply PostgreSQL credentials and a strong SQL Server
password, and accept the emulator and SQL Server license terms before setting
ACCEPT_EULA=Y. Run:

```powershell
docker compose config --quiet
docker compose up --build -d
docker compose ps
curl.exe -i "http://localhost:8081/api/stories/best?qtyStories=10"
```

### Kubernetes (Docker Desktop kind)

Use a dedicated namespace, epapagno-hackernews. Create the namespace first:

```powershell
kubectl apply -f k8s/namespace.yaml
```

Create the following Secrets in that namespace using your own credentials.
Do not commit real secrets.

| Secret | Required keys |
| --- | --- |
| epapagno-postgres-secret | POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_DB |
| epapagno-application-secret | ConnectionStrings__Postgres |
| epapagno-emulator-secret | ACCEPT_EULA, MSSQL_SA_PASSWORD |

The PostgreSQL connection string must use Host=epapagno-postgres and match
the PostgreSQL Secret. ACCEPT_EULA must be Y only after accepting the terms.

Build and import both local images:

```powershell
docker build -t epapagno-hackernews-api:2.0 -f dockerfile .
docker build -t epapagno-persistence-worker:2.0 -f worker.dockerfile .
docker save -o hackernews-2.0.tar epapagno-hackernews-api:2.0 epapagno-persistence-worker:2.0
docker cp hackernews-2.0.tar desktop-control-plane:/root/hackernews-2.0.tar
docker exec desktop-control-plane ctr -n k8s.io images import /root/hackernews-2.0.tar
kubectl apply -n epapagno-hackernews -f k8s/
kubectl get pods,svc,pvc -n epapagno-hackernews
```

### Validation checklist

- Build and run unit tests before deploying.
- First request on empty stores: External; subsequent requests: Cache.
- Verify worker logs and relational rows in PostgreSQL.
- Delete only the Redis snapshot in a test environment and verify Local recovery.
- Verify old or duplicate messages cannot replace a newer ranking.
- Verify stale responses preserve the original retrieval timestamp.
- Verify the API returns 503 when no usable data exists and the provider fails.
- Verify the three X-Data-* headers in Swagger.

The emulator is for local development only. DLQ replay and recovery of failed
broker publications are deferred. Do not assume emulator messages survive restarts.
