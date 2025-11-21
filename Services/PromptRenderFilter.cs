using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Filter that tracks prompts (rendered input) and logs them to the database for Live Monitor display.
    /// This filter captures the rendered prompt before it's sent to the model.
    /// </summary>
    public class PromptRenderFilter : IPromptRenderFilter
    {
        private readonly ICustomLogger _customLogger;
        private readonly string _modelName;

        public PromptRenderFilter(ICustomLogger customLogger, string modelName)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _modelName = modelName ?? "unknown";
        }

        public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
        {
            // Call the next filter to render the prompt
            await next(context);

            try
            {
                // Log the rendered prompt after it's been processed
                var renderedPrompt = context.RenderedPrompt ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(renderedPrompt))
                {
                    // Truncate very long prompts for readability in logs (keep first 1000 chars)
                    var displayPrompt = renderedPrompt.Length > 1000 
                        ? renderedPrompt.Substring(0, 1000) + "..." 
                        : renderedPrompt;
                    
                    var message = $"[{_modelName}] PROMPT: {displayPrompt}";
                    _customLogger?.Log("Information", "ModelPrompt", message);
                }
            }
            catch
            {
                // Best-effort: don't fail if logging fails
            }
        }
    }
}
