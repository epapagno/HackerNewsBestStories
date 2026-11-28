using HackerNewsBestStories.Api.Models;
using HackerNewsBestStories.Api.Options;
using HackerNewsBestStories.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories.Api.Controllers;

[ApiController]
[Route("api/stories")]
public sealed class StoriesController : ControllerBase
{
    private readonly IBestStoriesService _bestStoriesService;
    private readonly HackerNewsOptions _options;

    public StoriesController(
        IBestStoriesService bestStoriesService,
        IOptions<HackerNewsOptions> options)
    {
        _bestStoriesService = bestStoriesService;
        _options = options.Value;
    }

    [HttpGet("best")]
    [ProducesResponseType(typeof(IReadOnlyList<StoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<StoryResponse>>> GetBestStories(
        [FromQuery] int qtyStories,
        CancellationToken cancellationToken)
    {
        if (qtyStories < 1 || qtyStories > _options.MaxRequestedStories)
        {
            return BadRequest(new
            {
                error = $"qtyStories must be between 1 and {_options.MaxRequestedStories}."
            });
        }

        try
        {
            var result = await _bestStoriesService.GetBestStoriesResultAsync(qtyStories, cancellationToken);
            Response.Headers["X-Data-Origin"] = result.Origin.ToString();
            Response.Headers["X-Data-Fetched-At"] = result.FetchedAt.UtcDateTime.ToString("O");
            Response.Headers["X-Data-Is-Stale"] = result.IsStale ? "true" : "false";
            return Ok(result.Stories);
        }
        catch (HttpRequestException)
        {
            return Problem(
                title: "Hacker News API is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(
                title: "Hacker News API timed out.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
