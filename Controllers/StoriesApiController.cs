using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
namespace TinyGenerator.Controllers;

[ApiController]
[Route("api/stories")]
public class StoriesApiController : ControllerBase
{
    private readonly StoriesService _stories;
    private readonly ILogger<StoriesApiController> _logger;

    public StoriesApiController(StoriesService stories, ILogger<StoriesApiController> logger)
    {
        _stories = stories;
        _logger = logger;
    }

    [HttpGet("{id:int}/evaluations")]
    public IActionResult GetEvaluations(int id)
    {
        try
        {
            var evals = _stories.GetEvaluationsForStory(id);
            return Ok(evals);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error returning evaluations for story {Id}", id);
            return StatusCode(500);
        }
    }
}
