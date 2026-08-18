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

From Swagger UI, expand `GET /api/stories/best`, click **Try it out**, provide a value for `n` (for example `10`), and execute the request.

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

`n` must be between 1 and 500. If Hacker News exposes fewer than `n` best stories at that moment, the API returns the stories that are available.

## Approach

The Hacker News `beststories` endpoint returns story IDs. I do not assume those IDs are strictly ordered by score, because the API documentation does not make that guarantee. The application therefore:

1. Retrieves the current best-story IDs.
2. Retrieves the details for those stories with bounded parallelism.
3. Sorts the valid stories by `score` descending.
4. Caches that ranked list in memory.
5. Returns the first `n` entries from the cached list.

The same ranked cache is shared for every value of `n`, so a request for `qtyStories=10` followed by `qtyStories=20` does not trigger another Hacker News refresh while the cache is valid.

## Protecting the Hacker News API

Two simple measures keep request volume bounded:

- The ranked result is cached in memory for 60 seconds by default.
- A `SemaphoreSlim` allows only one cache refresh at a time, preventing a cache stampede when many callers arrive after expiration.
- Story-detail calls during a refresh use bounded parallelism (`20` by default) instead of firing every request at once.

These values are configurable in `appsettings.json`.

This is intentionally kept in-process and dependency-light for the exercise. For a single application instance it means a large number of callers are normally served without additional Hacker News requests during the cache window.

## Assumptions

- `uri` maps directly to Hacker News' optional `url` field, so it can be `null` for stories such as Ask HN posts that do not have an external URL.
- `commentCount` maps to the Hacker News `descendants` field.
- Deleted, dead, null, or malformed story records are skipped.
- When two stories have the same score, newer stories are returned first to keep ordering deterministic.
- A maximum caller value of 500 is used as a defensive API limit. If fewer best-story IDs are available, the API returns the available set.

## What I would improve with more time

- Add integration tests around the HTTP endpoint and error responses.
- Add retry/backoff and a circuit breaker for transient Hacker News failures, after defining the desired failure behaviour rather than adding retries blindly.
- Use a distributed cache such as Redis if the API needed to run on multiple instances, so every instance does not refresh Hacker News independently.
- Consider stale-while-revalidate/background refresh so callers never pay the refresh latency after cache expiry.
- Add metrics around cache hits, refresh duration, upstream failures and request counts before tuning cache duration or concurrency.
- Add container/deployment configuration if the target environment required it.
