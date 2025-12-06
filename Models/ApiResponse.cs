using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TinyGenerator.Models
{
    /// <summary>
    /// Represents a structured API response from LLM models.
    /// Used for robust deserialization with fallback parsing.
    /// </summary>
    public class ApiResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("message")]
        public ApiMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; set; }

        [JsonPropertyName("total_duration")]
        public long? TotalDuration { get; set; }

        [JsonPropertyName("load_duration")]
        public long? LoadDuration { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; set; }

        [JsonPropertyName("prompt_eval_duration")]
        public long? PromptEvalDuration { get; set; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long? EvalDuration { get; set; }
    }

    public class ApiMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<ApiToolCall>? ToolCalls { get; set; }
    }

    public class ApiToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("function")]
        public ApiFunction? Function { get; set; }
    }

    public class ApiFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public object? Arguments { get; set; }
    }

    /// <summary>
    /// Result of TTS schema validation including coverage analysis.
    /// </summary>
    public class TtsValidationResult
    {
        public bool IsValid { get; set; }
        public double CoveragePercent { get; set; }
        public int OriginalChars { get; set; }
        public int CoveredChars { get; set; }
        public int RemainingChars { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? FeedbackMessage { get; set; }
        // When true, the feedback message should be injected as a system message
        // on the next retry instead of being included inside the user prompt.
        public bool ShouldInjectAsSystem { get; set; } = false;
        public List<ParsedToolCall> ExtractedToolCalls { get; set; } = new();
    }

    /// <summary>
    /// Simplified tool call representation for validation.
    /// </summary>
    public class ParsedToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public Dictionary<string, object> Arguments { get; set; } = new();
    }
}
