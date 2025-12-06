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
        private readonly ICustomLogger? _logger;

        [BindProperty]
        public string Theme { get; set; } = "A mysterious adventure in the enchanted forest";

        [BindProperty]
        public string ModelEndpoint { get; set; } = "http://localhost:11434/v1";

        [BindProperty]
        public string ApiKey { get; set; } = "ollama-dummy-key";

        [BindProperty]
        public string WriterModels { get; set; } = "LongWriter-llama3.1-8b-GGUF:Q4_K_M";

        [BindProperty]
        public string EvaluatorModel { get; set; } = "qwen3:4b-instruct";

        public StoryGenerationResult? Result { get; set; }

        public LangChainTestModel(ICustomLogger? logger = null)
        {
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

                _logger?.Log("Warning", "LangChainTest", "Legacy LangChainStoryGenerationService removed. Use StartMultiStepStoryCommand flow instead.");
                Result = new StoryGenerationResult
                {
                    Success = false,
                    Message = "Legacy LangChainStoryGenerationService has been removed. Use the multi-step command flow (StartMultiStepStoryCommand)."
                };
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
