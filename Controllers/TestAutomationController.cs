using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;
using TinyGenerator.Models;

namespace TinyGenerator.Controllers;

/// <summary>
/// API controller for automated testing of story operations
/// Uses CommandDispatcher pattern for all operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TestAutomationController : ControllerBase
{
    private readonly CommandDispatcher _dispatcher;
    private readonly StoriesService _storiesService;
    private readonly DatabaseService _database;
    private readonly ICustomLogger _customLogger;

    public TestAutomationController(
        CommandDispatcher dispatcher,
        StoriesService storiesService,
        DatabaseService database,
        ICustomLogger customLogger)
    {
        _dispatcher = dispatcher;
        _storiesService = storiesService;
        _database = database;
        _customLogger = customLogger;
    }

    /// <summary>
    /// Generate a new story with multi-step orchestration
    /// POST /api/TestAutomation/generate-story
    /// Body: { "theme": "test theme", "writerAgentId": 1 }
    /// </summary>
    [HttpPost("generate-story")]
    public IActionResult GenerateStory([FromBody] GenerateStoryRequest request)
    {
        return Ok(new
        {
            success = false,
            message = "Story generation via API not yet implemented. Use the web interface for now."
        });
    }

    /// <summary>
    /// Evaluate a story with specified agent
    /// POST /api/TestAutomation/evaluate-story
    /// Body: { "storyId": 36, "agentId": 1 }
    /// </summary>
    [HttpPost("evaluate-story")]
    public IActionResult EvaluateStory([FromBody] EvaluateStoryRequest request)
    {
        try
        {
            _customLogger.Log("Info", "TestAutomation", $"Enqueuing story evaluation: storyId={request.StoryId}, agentId={request.AgentId}");

            var agent = _database.GetAgentById(request.AgentId);
            var isCoherence = agent?.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;

            var metadata = new Dictionary<string, string>
            {
                ["storyId"] = request.StoryId.ToString(),
                ["agentId"] = request.AgentId.ToString(),
                ["agentName"] = agent?.Name ?? "unknown",
                ["evaluationType"] = isCoherence ? "coherence" : "standard"
            };

            var handle = _dispatcher.Enqueue(
                "test_story_evaluation",
                async ctx =>
                {
                    var (success, score, error) = await _storiesService.EvaluateStoryWithAgentAsync(request.StoryId, request.AgentId);
                    
                    if (!success)
                    {
                        return new CommandResult(false, error ?? "Evaluation failed");
                    }

                    if (isCoherence)
                    {
                        var globalCoherence = _database.GetGlobalCoherence((int)request.StoryId);
                        return new CommandResult(true, $"Coherence evaluation completed. Score: {score:F2}, Global coherence: {globalCoherence?.GlobalCoherenceValue:F2}");
                    }

                    return new CommandResult(true, $"Standard evaluation completed. Score: {score:F2}");
                },
                threadScope: "test_automation",
                metadata: metadata);

            return Ok(new
            {
                success = true,
                commandId = handle.RunId,
                evaluationType = isCoherence ? "coherence" : "standard",
                message = "Evaluation enqueued"
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Generate TTS for a story
    /// POST /api/TestAutomation/generate-tts
    /// Body: { "storyId": 36, "voiceId": 1 }
    /// </summary>
    [HttpPost("generate-tts")]
    public IActionResult GenerateTts([FromBody] GenerateTtsRequest request)
    {
        try
        {
            _customLogger.Log("Info", "TestAutomation", $"Enqueuing TTS generation: storyId={request.StoryId}, voiceId={request.VoiceId}");

            var story = _storiesService.GetStoryById(request.StoryId);
            if (story == null)
            {
                return Ok(new { success = false, error = "Story not found" });
            }

            // Unified path: single dispatcher command, storyId only.
            // voiceId is currently ignored by the TTS pipeline (voices are assigned from tts_schema.json).
                var enqueue = _storiesService.TryEnqueueGenerateTtsAudioCommand(request.StoryId, trigger: "test_api", priority: 3);
                if (!enqueue.Enqueued)
                {
                    return BadRequest(enqueue.Message);
            }

                return Ok(new
                {
                    success = true,
                    commandId = enqueue.RunId,
                    message = "TTS generation enqueued"
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get command status
    /// GET /api/TestAutomation/command/{commandId}
    /// </summary>
    [HttpGet("command/{commandId}")]
    public IActionResult GetCommandStatus(string commandId)
    {
        try
        {
            // CommandDispatcher doesn't expose GetHandle, so we check if task is complete by waiting with timeout
            // For now, just return a simple status
            return Ok(new
            {
                success = true,
                commandId,
                message = "Command status tracking not yet implemented. Check logs for results."
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get coherence evaluation results for a story
    /// GET /api/TestAutomation/coherence/{storyId}
    /// </summary>
    [HttpGet("coherence/{storyId}")]
    public IActionResult GetCoherenceResults(int storyId)
    {
        try
        {
            var chunkFacts = _database.GetAllChunkFacts(storyId);
            var coherenceScores = _database.GetCoherenceScores(storyId);
            var globalCoherence = _database.GetGlobalCoherence(storyId);

            return Ok(new
            {
                success = true,
                storyId,
                chunkFactsCount = chunkFacts?.Count ?? 0,
                coherenceScoresCount = coherenceScores?.Count ?? 0,
                globalCoherence = globalCoherence != null ? new
                {
                    value = globalCoherence.GlobalCoherenceValue,
                    chunkCount = globalCoherence.ChunkCount,
                    notes = globalCoherence.Notes
                } : null,
                chunkFacts = chunkFacts?.Select(cf => new
                {
                    chunkNumber = cf.ChunkNumber,
                    hasFacts = !string.IsNullOrEmpty(cf.FactsJson),
                    factsJson = cf.FactsJson
                }),
                coherenceScores = coherenceScores?.Select(cs => new
                {
                    chunkNumber = cs.ChunkNumber,
                    localCoherence = cs.LocalCoherence,
                    globalCoherence = cs.GlobalCoherence,
                    hasErrors = !string.IsNullOrEmpty(cs.Errors)
                })
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get story details
    /// GET /api/TestAutomation/story/{storyId}
    /// </summary>
    [HttpGet("story/{storyId}")]
    public IActionResult GetStory(long storyId)
    {
        try
        {
            var story = _storiesService.GetStoryById(storyId);
            if (story == null)
            {
                return Ok(new { success = false, error = "Story not found" });
            }

            return Ok(new
            {
                success = true,
                story = new
                {
                    id = story.Id,
                    generationId = story.GenerationId,
                    prompt = story.Prompt,
                    length = story.StoryRaw?.Length ?? 0,
                    model = story.Model,
                    agent = story.Agent,
                    score = story.Score,
                    status = story.Status,
                    timestamp = story.Timestamp
                }
            });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Get list of agents by role
    /// GET /api/TestAutomation/agents?role=coherence_evaluator
    /// </summary>
    [HttpGet("agents")]
    public IActionResult GetAgents([FromQuery] string? role = null)
    {
        try
        {
            var agents = _database.ListAgents()
                .Where(a => a.IsActive && (role == null || a.Role == role))
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    role = a.Role,
                    modelId = a.ModelId
                })
                .ToList();

            return Ok(new
            {
                success = true,
                count = agents.Count,
                agents
            });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Scan stories_folder and set status to audio_master_generated for folders containing final_mix.wav
    /// POST /api/TestAutomation/scan-audio-masters
    /// </summary>
    [HttpPost("scan-audio-masters")]
    public IActionResult ScanAudioMasters()
    {
        try
        {
            _customLogger.Log("Info", "TestAutomation", "Enqueuing audio masters scan");

            var metadata = new Dictionary<string, string>
            {
                ["task"] = "scan_audio_masters"
            };

            var handle = _dispatcher.Enqueue(
                "scan_audio_masters",
                async ctx =>
                {
                    var updated = await _storiesService.ScanAndMarkAudioMastersAsync();
                    return new CommandResult(true, $"Scan completed. Updated {updated} stories.");
                },
                threadScope: "maintenance",
                metadata: metadata);

            return Ok(new
            {
                success = true,
                commandId = handle.RunId,
                message = "Scan enqueued"
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}

public class GenerateStoryRequest
{
    public string Theme { get; set; } = "";
    public string WriterType { get; set; } = "A";
    public string? PlanName { get; set; }
}

public class EvaluateStoryRequest
{
    public long StoryId { get; set; }
    public int AgentId { get; set; }
}

public class GenerateTtsRequest
{
    public long StoryId { get; set; }
    public int VoiceId { get; set; }
    public string? OutputFolder { get; set; }
}
