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

[ApiController]
[Route("api/utils")]
public class UtilsApiController : ControllerBase
{
    private readonly ILogger<UtilsApiController> _logger;

    public UtilsApiController(ILogger<UtilsApiController> logger)
    {
        _logger = logger;
    }

    [HttpPost("check-url")]
    public async Task<IActionResult> CheckUrl([FromBody] UrlCheckRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request?.Url))
            {
                return BadRequest(new { exists = false, error = "URL vuoto" });
            }

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                
                try
                {
                    var response = await client.GetAsync(request.Url);
                    // Accept 200-399 as valid (includes redirects)
                    bool exists = (int)response.StatusCode < 400;
                    
                    return Ok(new 
                    { 
                        exists,
                        statusCode = (int)response.StatusCode,
                        url = request.Url
                    });
                }
                catch (HttpRequestException)
                {
                    return Ok(new { exists = false, error = "Errore connessione", url = request.Url });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking URL: {Url}", request?.Url);
            return StatusCode(500, new { exists = false, error = "Errore server" });
        }
    }
}

public class UrlCheckRequest
{
    public string? Url { get; set; }
}
