using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for story persistence (CRUD operations).
    /// Converted from StoryWriterSkill (Semantic Kernel).
    /// </summary>
    public class StoryWriterTool : BaseLangChainTool, ITinyTool
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService? _database;
        public string? LastResult { get; set; }

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public StoryWriterTool(StoriesService stories, DatabaseService? database = null, ICustomLogger? logger = null) 
            : base("storywriter", "Provides CRUD operations for stories in the database.", logger)
        {
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _database = database;
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                Name,
                Description,
                new Dictionary<string, object>
                {
                    {
                        "operation",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The operation: 'create_story', 'read_story', 'update_story', 'delete_story', 'describe'" }
                        }
                    },
                    {
                        "story",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Story text (for create_story/update_story)" }
                        }
                    },
                    {
                        "id",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Story id (for read_story/update_story/delete_story)" }
                        }
                    },
                    {
                        "status",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Story status code (stories_status.code value used for create/update)" }
                        }
                    }
                },
                new List<string> { "operation" }
            );
        }

        public override Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<StoryWriterToolRequest>(input);
                if (request == null)
                    return Task.FromResult(SerializeResult(new { error = "Invalid input format" }));

                CustomLogger?.Log("Info", "StoryWriterTool", $"Executing operation: {request.Operation}");

                var result = request.Operation?.ToLowerInvariant() switch
                {
                    "create_story" => ExecuteCreateStory(request),
                    "read_story" => ExecuteReadStory(request),
                    "update_story" => ExecuteUpdateStory(request),
                    "delete_story" => ExecuteDeleteStory(request),
                    "describe" => SerializeResult(new { result = "Available operations: create_story(story), read_story(id), update_story(id, story?, status?), delete_story(id). Returns JSON confirmation." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "StoryWriterTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return Task.FromResult(SerializeResult(new { error = ex.Message }));
            }
        }

        private string ExecuteCreateStory(StoryWriterToolRequest request)
        {
            try
            {
                var modelInfo = ModelName ?? (_database != null && ModelId.HasValue ? _database.GetModelInfoById(ModelId.Value)?.Name : null) ?? string.Empty;
                int? statusId = null;
                if (!string.IsNullOrWhiteSpace(request.Status))
                {
                    statusId = ResolveStatusId(request.Status);
                    if (statusId == null)
                    {
                        return SerializeResult(new { error = $"Unknown status code '{request.Status}'" });
                    }
                }

                var id = _stories.InsertSingleStory(string.Empty, request.Story ?? string.Empty, ModelId, AgentId, 0.0, null, 0, statusId, memoryKey: null);
                var obj = new { id, story = request.Story, model = modelInfo, model_id = ModelId, agent_id = AgentId, status = request.Status, status_id = statusId };
                LastResult = JsonSerializer.Serialize(obj);
                return LastResult;
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteReadStory(StoryWriterToolRequest request)
        {
            try
            {
                if (!request.Id.HasValue)
                    return SerializeResult(new { error = "id is required for read_story" });

                var row = _stories.GetStoryById(request.Id.Value);
                if (row == null)
                    return SerializeResult(new { error = "not found", id = request.Id });

                var obj = new
                {
                    id = row.Id,
                    generation_id = row.GenerationId,
                    memory_key = row.MemoryKey,
                    ts = row.Timestamp,
                    prompt = row.Prompt,
                    story = row.Story,
                    model = row.Model,
                    agent = row.Agent,
                    eval = row.Eval,
                    score = row.Score,
                    approved = row.Approved,
                    status = row.Status,
                    status_id = row.StatusId,
                    status_description = row.StatusDescription
                };
                LastResult = JsonSerializer.Serialize(obj);
                return LastResult;
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteUpdateStory(StoryWriterToolRequest request)
        {
            try
            {
                if (!request.Id.HasValue)
                    return SerializeResult(new { error = "id is required for update_story" });

                var existing = _stories.GetStoryById(request.Id.Value);
                if (existing == null)
                    return SerializeResult(new { error = "not found", id = request.Id });

                var newStory = request.Story ?? existing.Story;
                int? statusId = existing.StatusId;
                var statusCode = existing.Status;
                var statusProvided = request.Status != null;
                if (statusProvided)
                {
                    if (string.IsNullOrWhiteSpace(request.Status))
                    {
                        statusId = null;
                        statusCode = string.Empty;
                    }
                    else
                    {
                        var resolved = ResolveStatusId(request.Status);
                        if (resolved == null)
                        {
                            return SerializeResult(new { error = $"Unknown status code '{request.Status}'" });
                        }
                        statusId = resolved;
                        statusCode = request.Status;
                    }
                }

                _stories.UpdateStoryById(request.Id.Value, newStory, statusId: statusId, updateStatus: statusProvided);

                var obj = new { id = request.Id, story = newStory, status = statusCode, status_id = statusId };
                LastResult = JsonSerializer.Serialize(obj);
                return LastResult;
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteDeleteStory(StoryWriterToolRequest request)
        {
            try
            {
                if (!request.Id.HasValue)
                    return SerializeResult(new { error = "id is required for delete_story" });

                // DeleteStory not available - mark as deprecated or skip
                return SerializeResult(new { result = "delete_story operation not available", id = request.Id });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private class StoryWriterToolRequest
        {
            public string? Operation { get; set; }
            public long? Id { get; set; }
            public string? Story { get; set; }
            public string? Status { get; set; }
        }

        private int? ResolveStatusId(string? statusCode)
        {
            try
            {
                return _stories.ResolveStatusId(statusCode);
            }
            catch
            {
                return null;
            }
        }
    }
}
