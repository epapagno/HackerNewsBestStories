using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using HackerNewsBestStories.Api.Controllers;

namespace HackerNewsBestStories.Api.Services;

public sealed class RankingHeadersFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(StoriesController) ||
            context.MethodInfo.Name != nameof(StoriesController.GetBestStories) ||
            operation.Responses is null ||
            !operation.Responses.TryGetValue("200", out var response) ||
            response is not OpenApiResponse success)
            return;

        success.Headers ??= new Dictionary<string, IOpenApiHeader>();
        success.Headers["X-Data-Origin"] = new OpenApiHeader
        {
            Description = "Origin of the returned ranking: Cache, Local or External.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        };
        success.Headers["X-Data-Fetched-At"] = new OpenApiHeader
        {
            Description = "Original retrieval time in UTC (ISO 8601).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" }
        };
        success.Headers["X-Data-Is-Stale"] = new OpenApiHeader
        {
            Description = "True when the ranking has reached its configured freshness limit (six hours by default).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean }
        };
    }
}
