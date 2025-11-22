using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain version of TextPlugin.
    /// Converts string manipulation functions to LangChain tools with JSON schema.
    /// </summary>
    public class TextTool : BaseLangChainTool, ITinyTool
    {
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }
        public string? LastCalled { get; set; }

        public TextTool(ICustomLogger? logger = null) 
            : base("text", "Text manipulation functions for uppercase, lowercase, trimming, substring, join, split operations", logger)
        {
        }

        public override Dictionary<string, object> GetSchema()
        {
            var functionEnum = new List<object> { "toupper", "tolower", "trim", "length", "substring", "join", "split" };
            
            var properties = new Dictionary<string, object>
            {
                { "function", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "enum", functionEnum },
                        { "description", "The text function to call" }
                    }
                },
                { "text", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "The input text" }
                    }
                },
                { "startIndex", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Start index for substring" }
                    }
                },
                { "length", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Length for substring or join" }
                    }
                },
                { "separator", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Separator for join/split" }
                    }
                }
            };

            return CreateFunctionSchema("text", "Text manipulation functions", properties, new List<string> { "function" });
        }

        public override async Task<string> ExecuteAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<TextToolInput>(jsonInput, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                object result;
                var funcLower = input.Function?.ToLower();
                
                if (funcLower == "toupper")
                    result = new { result = ToUpper(input.Text ?? "") };
                else if (funcLower == "tolower")
                    result = new { result = ToLower(input.Text ?? "") };
                else if (funcLower == "trim")
                    result = new { result = Trim(input.Text ?? "") };
                else if (funcLower == "length")
                    result = new { result = Length(input.Text ?? "") };
                else if (funcLower == "substring")
                    result = new { result = Substring(input.Text ?? "", input.StartIndex ?? 0, input.Length ?? 0) };
                else if (funcLower == "join")
                    result = new { result = Join(input.Array ?? Array.Empty<string>(), input.Separator ?? "") };
                else if (funcLower == "split")
                    result = new { result = Split(input.Text ?? "", input.Separator ?? "") };
                else
                    result = new { error = $"Unknown function: {input.Function}" };

                LastCalled = input.Function;
                LastFunctionCalled = input.Function;
                LastFunctionResult = JsonSerializer.Serialize(result);
                CustomLogger?.Log("Info", "TextTool", $"Executed {input.Function}");
                
                return await Task.FromResult(JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TextTool", $"Execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private string ToUpper(string input)
        {
            return input.ToUpperInvariant();
        }

        private string ToLower(string input)
        {
            return input.ToLowerInvariant();
        }

        private string Trim(string input)
        {
            return input.Trim();
        }

        private int Length(string input)
        {
            return input?.Length ?? 0;
        }

        private string Substring(string input, int startIndex, int length)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            if (startIndex < 0) startIndex = 0;
            if (length <= 0) return string.Empty;
            if (startIndex >= input.Length) return string.Empty;
            if (startIndex + length > input.Length) length = input.Length - startIndex;
            return input.Substring(startIndex, length);
        }

        private string Join(string[] input, string separator)
        {
            return string.Join(separator, input);
        }

        private string[] Split(string input, string separator)
        {
            return input.Split(separator);
        }
    }

    public class TextToolInput
    {
        [JsonPropertyName("function")]
        public string? Function { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("startIndex")]
        public int? StartIndex { get; set; }

        [JsonPropertyName("length")]
        public int? Length { get; set; }

        [JsonPropertyName("array")]
        public string[]? Array { get; set; }

        [JsonPropertyName("separator")]
        public string? Separator { get; set; }
    }
}
