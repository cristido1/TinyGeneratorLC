using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Stories
{
    public class EditModel : PageModel
    {
        private readonly StoriesService _stories;
        private readonly DatabaseService _database;

        public EditModel(StoriesService stories, DatabaseService database)
        {
            _stories = stories;
            _database = database;
        }

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Tab { get; set; }

        [BindProperty]
        public string Prompt { get; set; } = string.Empty;

        [BindProperty]
        public string StoryText { get; set; } = string.Empty;

        [BindProperty]
        public string? StoryRevisedText { get; set; }

        [BindProperty]
        public string? StoryTaggedText { get; set; }

        [BindProperty]
        public string? Characters { get; set; }

        [BindProperty]
        public int? StatusId { get; set; }

        [BindProperty]
        public int? AgentId { get; set; }

        [BindProperty]
        public int? ModelId { get; set; }

    [BindProperty]
    public int? SerieId { get; set; }

    [BindProperty]
    public int? SerieEpisode { get; set; }

    public List<StoryStatus> Statuses { get; set; } = new();
    public List<Agent> Agents { get; set; } = new();
    public List<TinyGenerator.Models.ModelInfo> Models { get; set; } = new();
    public List<TinyGenerator.Models.Series> Series { get; set; } = new();

    [BindProperty]
    public string? Title { get; set; }

    public string FormatterAgentName { get; set; } = string.Empty;
    public List<CharacterVoiceEntry> CharacterVoices { get; set; } = new();
    public List<VoiceOption> AvailableVoices { get; set; } = new();

        [BindProperty]
        public List<CharacterVoiceEntry> PostedCharacterVoices { get; set; } = new();

        public IActionResult OnGet(long id)
        {
            Id = id;
            var s = _stories.GetStoryById(id);
            if (s == null) return NotFound();
            Prompt = s.Prompt;
            StoryText = s.StoryRaw;
            StoryRevisedText = s.StoryRevised;
            StoryTaggedText = s.StoryTagged;
            Characters = s.Characters;
            StatusId = s.StatusId;
            Title = s.Title;
            AgentId = s.AgentId;
            ModelId = s.ModelId;
            SerieId = s.SerieId;
            SerieEpisode = s.SerieEpisode;
            LoadStatuses();
            LoadAgents();
            LoadModels();
            LoadSeries();
            LoadVoicesAndCharactersFromSchema(s);
            FormatterAgentName = ResolveFormatterAgentName(s.FormatterModelId);
            return Page();
        }

        public IActionResult OnPost()
        {
            if (Id <= 0) return BadRequest();
            var story = _stories.GetStoryById(Id);
            if (story == null) return NotFound();
            LoadStatuses();
            _stories.UpdateStoryById(Id, StoryText, ModelId, AgentId, StatusId, updateStatus: true, allowCreatorMetadataUpdate: true);

            if (StoryRevisedText != null)
            {
                _database.UpdateStoryRevised(Id, StoryRevisedText ?? string.Empty);
            }

            if (StoryTaggedText != null)
            {
                _database.UpdateStoryTagged(
                    Id,
                    StoryTaggedText ?? string.Empty,
                    story.FormatterModelId,
                    story.FormatterPromptHash,
                    story.StoryTaggedVersion);
            }
            if (!string.IsNullOrEmpty(Characters))
            {
                _stories.UpdateStoryCharacters(Id, Characters);
            }
            _stories.UpdateStoryTitle(Id, Title ?? string.Empty);
            if (!SerieId.HasValue)
            {
                SerieEpisode = null;
            }
            _database.UpdateStorySeriesInfo(Id, SerieId, SerieEpisode, allowSeriesUpdate: true);
            SavePostedVoicesToSchema(story, PostedCharacterVoices);
            return RedirectToPage("/Stories/Details", new { id = Id });
        }

        public async Task<IActionResult> OnPostReassignVoicesAsync()
        {
            if (Id <= 0) return BadRequest();
            var story = _stories.GetStoryById(Id);
            if (story == null) return NotFound();

            var (ok, msg) = await _stories.AssignVoicesAsync(Id);
            if (!ok)
            {
                TempData["ErrorMessage"] = string.IsNullOrWhiteSpace(msg)
                    ? "Riassegnazione voci fallita."
                    : msg;
            }
            else
            {
                TempData["StatusMessage"] = string.IsNullOrWhiteSpace(msg)
                    ? "Voci riassegnate."
                    : msg;
            }

            return RedirectToPage(new { id = Id, tab = "personaggi" });
        }

        private void LoadStatuses()
        {
            try
            {
                Statuses = _stories.GetAllStoryStatuses();
            }
            catch
            {
                Statuses = new List<StoryStatus>();
            }
        }

        private void LoadAgents()
        {
            try
            {
                Agents = _database.ListAgents().Where(a => a.IsActive).ToList();
            }
            catch
            {
                Agents = new List<Agent>();
            }
        }

        private void LoadModels()
        {
            try
            {
                Models = _database.ListModels().ToList();
            }
            catch
            {
                Models = new List<TinyGenerator.Models.ModelInfo>();
            }
        }
        private void LoadSeries()
        {
            try
            {
                Series = _database.ListAllSeries();
            }
            catch
            {
                Series = new List<TinyGenerator.Models.Series>();
            }
        }

        private string ResolveFormatterAgentName(int? formatterModelId)
        {
            try
            {
                var agents = _database.ListAgents()
                    .Where(a => a.IsActive && string.Equals(a.Role, "formatter", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (agents.Count == 0) return string.Empty;

                if (formatterModelId.HasValue)
                {
                    var match = agents.FirstOrDefault(a => a.ModelId == formatterModelId.Value);
                    if (match != null) return match.Name ?? string.Empty;
                }

                return agents[0].Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void LoadVoicesAndCharactersFromSchema(StoryRecord story)
        {
            try
            {
                var voices = _database.ListTtsVoices(onlyEnabled: true);
                AvailableVoices = voices
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                    .OrderBy(v => v.Name)
                    .Select(v => new VoiceOption
                    {
                        VoiceId = v.VoiceId,
                        Name = string.IsNullOrWhiteSpace(v.Name) ? v.VoiceId : v.Name,
                        Gender = v.Gender ?? string.Empty,
                        Age = v.Age ?? string.Empty,
                        Archetype = v.Archetype ?? string.Empty,
                        TemplateUrl = BuildTemplateUrl(v.TemplateWav),
                        SampleUrl = "/TtsVoices?handler=Sample&voiceId=" + Uri.EscapeDataString(v.VoiceId)
                    })
                    .ToList();
            }
            catch
            {
                AvailableVoices = new List<VoiceOption>();
            }

            CharacterVoices = ReadCharacterVoicesFromSchema(story);
        }

        private List<CharacterVoiceEntry> ReadCharacterVoicesFromSchema(StoryRecord story)
        {
            var result = new List<CharacterVoiceEntry>();
            var schemaPath = GetSchemaPath(story);
            if (string.IsNullOrWhiteSpace(schemaPath) || !System.IO.File.Exists(schemaPath))
                return result;

            try
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(schemaPath)) as JsonObject;
                if (root == null) return result;

                var charsNode = GetNodeCaseInsensitive(root, "characters", "Characters");
                if (charsNode is not JsonArray charsArray) return result;

                for (int i = 0; i < charsArray.Count; i++)
                {
                    if (charsArray[i] is not JsonObject ch) continue;
                    result.Add(new CharacterVoiceEntry
                    {
                        Index = i,
                        Name = ReadStringCaseInsensitive(ch, "name", "Name") ?? string.Empty,
                        Gender = ReadStringCaseInsensitive(ch, "gender", "Gender") ?? string.Empty,
                        VoiceId = ReadStringCaseInsensitive(ch, "voiceId", "VoiceId") ?? string.Empty,
                        Voice = ReadStringCaseInsensitive(ch, "voice", "Voice") ?? string.Empty
                    });
                }
            }
            catch
            {
                // best effort
            }

            return result;
        }

        private void SavePostedVoicesToSchema(StoryRecord story, List<CharacterVoiceEntry>? postedRows)
        {
            if (postedRows == null || postedRows.Count == 0) return;

            var schemaPath = GetSchemaPath(story);
            if (string.IsNullOrWhiteSpace(schemaPath) || !System.IO.File.Exists(schemaPath))
                return;

            try
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(schemaPath)) as JsonObject;
                if (root == null) return;

                var charsNode = GetNodeCaseInsensitive(root, "characters", "Characters");
                if (charsNode is not JsonArray charsArray) return;

                var voiceNameById = _database.ListTtsVoices(onlyEnabled: false)
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                    .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var first = g.First();
                            return string.IsNullOrWhiteSpace(first.Name) ? first.VoiceId : first.Name;
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (var row in postedRows)
                {
                    if (row.Index < 0 || row.Index >= charsArray.Count) continue;
                    if (charsArray[row.Index] is not JsonObject ch) continue;

                    var newVoiceId = (row.VoiceId ?? string.Empty).Trim();
                    var newVoiceName = string.Empty;
                    if (!string.IsNullOrWhiteSpace(newVoiceId) &&
                        voiceNameById.TryGetValue(newVoiceId, out var resolvedName))
                    {
                        newVoiceName = resolvedName ?? string.Empty;
                    }

                    SetStringCaseInsensitive(ch, newVoiceId, "voiceId", "VoiceId", "voice_id");
                    SetStringCaseInsensitive(ch, newVoiceName, "voice", "Voice");
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                System.IO.File.WriteAllText(schemaPath, root.ToJsonString(options));
            }
            catch
            {
                // best effort
            }
        }

        private static JsonNode? GetNodeCaseInsensitive(JsonObject obj, params string[] names)
        {
            foreach (var n in names)
            {
                if (obj.TryGetPropertyValue(n, out var node))
                    return node;
            }

            foreach (var kvp in obj)
            {
                if (names.Any(n => string.Equals(n, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                    return kvp.Value;
            }

            return null;
        }

        private static string? ReadStringCaseInsensitive(JsonObject obj, params string[] names)
        {
            var node = GetNodeCaseInsensitive(obj, names);
            if (node == null) return null;
            if (node is JsonValue)
            {
                try { return node.GetValue<string>(); } catch { }
            }
            return node.ToString();
        }

        private static void SetStringCaseInsensitive(JsonObject obj, string value, params string[] preferredNames)
        {
            foreach (var key in obj.Select(k => k.Key).ToList())
            {
                if (preferredNames.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)))
                {
                    obj[key] = value;
                    return;
                }
            }

            var targetName = preferredNames.FirstOrDefault() ?? "value";
            obj[targetName] = value;
        }

        private static string? BuildTemplateUrl(string? templateWav)
        {
            if (string.IsNullOrWhiteSpace(templateWav)) return null;
            return "/data_voices_samples/" + Uri.EscapeDataString(templateWav.Trim());
        }

        private static string? GetSchemaPath(StoryRecord story)
        {
            if (story == null || string.IsNullOrWhiteSpace(story.Folder))
                return null;
            return Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder, "tts_schema.json");
        }

        public sealed class CharacterVoiceEntry
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Gender { get; set; } = string.Empty;
            public string VoiceId { get; set; } = string.Empty;
            public string Voice { get; set; } = string.Empty;
        }

        public sealed class VoiceOption
        {
            public string VoiceId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Gender { get; set; } = string.Empty;
            public string Age { get; set; } = string.Empty;
            public string Archetype { get; set; } = string.Empty;
            public string? TemplateUrl { get; set; }
            public string SampleUrl { get; set; } = string.Empty;
        }
    }
}
