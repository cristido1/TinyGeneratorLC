using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Pages
{
    public class LangChainTestModel : PageModel
    {
        private readonly LangChainStoryGenerationService _langChainService;
        private readonly ICustomLogger? _logger;

        [BindProperty]
        public string Theme { get; set; } = "A mysterious adventure in the enchanted forest";

        [BindProperty]
        public string ModelEndpoint { get; set; } = "http://localhost:11434/v1";

        [BindProperty]
        public string ApiKey { get; set; } = "ollama-dummy-key";

        [BindProperty]
        public string WriterModels { get; set; } = "phi3:mini-128k,mistral:7b-instruct-q4_K_M";

        [BindProperty]
        public string EvaluatorModel { get; set; } = "qwen2.5:3b";

        public StoryGenerationResult? Result { get; set; }

        public LangChainTestModel(LangChainStoryGenerationService langChainService, ICustomLogger? logger = null)
        {
            _langChainService = langChainService;
            _logger = logger;
        }

        public void OnGet()
        {
            // Display default form
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                _logger?.Log("Info", "LangChainTest", $"Starting generation for theme: {Theme}");

                if (string.IsNullOrWhiteSpace(Theme))
                    Theme = "A mysterious adventure in the enchanted forest";

                var writerModelsArray = WriterModels?.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? new[] { "phi3:mini" };

                Result = await _langChainService.GenerateStoriesAsync(
                    theme: Theme,
                    writerModels: writerModelsArray,
                    evaluatorModel: EvaluatorModel,
                    modelEndpoint: ModelEndpoint,
                    apiKey: ApiKey,
                    progress: msg => _logger?.Log("Info", "LangChainTest", msg),
                    ct: HttpContext.RequestAborted);

                _logger?.Log("Info", "LangChainTest", $"Generation completed: {Result.Message}");
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainTest", $"Generation failed: {ex.Message}", ex.ToString());
                Result = new StoryGenerationResult
                {
                    Success = false,
                    Message = $"Error: {ex.Message}"
                };
            }

            return Page();
        }
    }
}
