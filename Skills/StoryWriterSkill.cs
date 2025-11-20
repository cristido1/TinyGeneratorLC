using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides CRUD operations for stories in the database.")]
    public class StoryWriterSkill
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService? _database;
        private readonly long? _modelId;
        private readonly int? _agentId;
        private readonly string? _modelName;
        public string? LastResult { get; set; }

        public StoryWriterSkill(StoriesService stories, DatabaseService? database = null, long? modelId = null, int? agentId = null, string? modelName = null)
        {
            _stories = stories;
            _database = database;
            _modelId = modelId;
            _agentId = agentId;
            _modelName = modelName;
        }

        [KernelFunction("create_story"), Description("Create a single story row (writer only). Returns JSON with inserted id.")]
        public string CreateStory(string story)
        {
            // For writer-only skill, the program provides model/agent info via properties; set minimal defaults
            var modelInfo = _modelName != null ? _modelName : (_database != null && _modelId.HasValue ? _database.GetModelInfoById(_modelId.Value)?.Name : string.Empty) ?? string.Empty;
            var id = _stories.InsertSingleStory(string.Empty, story, _modelId, _agentId, 0.0, null, 0, null, memoryKey: null);
            var obj = new { id = id, story = story, model = modelInfo, model_id = _modelId, agent_id = _agentId };
            LastResult = JsonSerializer.Serialize(obj);
            return id.ToString();;
        }

        [KernelFunction("read_story"), Description("Read a single story by id. Returns JSON with story fields.")]
        public string ReadStory(long id)
        {
            var row = _stories.GetStoryById(id);
            if (row == null) return JsonSerializer.Serialize(new { error = "not found", id });
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
                status = row.Status
            };
            LastResult = JsonSerializer.Serialize(obj);
            return LastResult;
        }

        [KernelFunction("update_story"), Description("Update an existing story by id with optional fields (writer-only): story, status. Returns JSON confirmation.")]
        public string UpdateStory(long id, string? story = null, string? status = null)
        {
            var existing = _stories.GetStoryById(id);
            if (existing == null) return JsonSerializer.Serialize(new { error = "not found", id });
            // Build an update via StoriesService.Update flow if available; if not, use direct SQL by exposing method in StoriesService.
            // For now, implement a minimal update by calling SaveGeneration with a new GenerationResult if necessary.
            // Alternatively, update directly via DB using StoriesService.Delete & Insert. Simpler approach: insert a new replacement row with same generation_id values.
            // We will add a small method to StoriesService for updating a single row by id (if not present yet).
            try
            {
                var newStory = story ?? existing.Story;
                var newStatus = status ?? existing.Status;
                // Writer skill must not modify the model field — update only story and status
                var ok = _stories.UpdateStoryById(id, newStory, null, null, newStatus);
                var obj = new { id = id, updated = ok };
                LastResult = JsonSerializer.Serialize(obj);
                return LastResult;
            }
            catch (System.Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        [KernelFunction("delete_story"), Description("Delete a story and its generation group by id. Returns confirmation JSON.")]
        public string DeleteStory(long id)
        {
            try
            {
                _stories.Delete(id);
                var res = new { id = id, deleted = true };
                LastResult = JsonSerializer.Serialize(res);
                return LastResult;
            }
            catch (System.Exception ex)
            {
                return JsonSerializer.Serialize(new { id = id, deleted = false, error = ex.Message });
            }
        }
    }
}
