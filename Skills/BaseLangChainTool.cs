using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// Base class for LangChain tools converted from Semantic Kernel skills.
    /// Provides schema generation and execution interface compatible with LangChain agent loops.
    /// </summary>
    public abstract class BaseLangChainTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        protected ICustomLogger? CustomLogger { get; set; }

        protected BaseLangChainTool(string name, string description, ICustomLogger? logger = null)
        {
            Name = name;
            Description = description;
            CustomLogger = logger;
        }

        /// <summary>
        /// Returns the tool schema in JSON format compatible with OpenAI function calling.
        /// </summary>
        public abstract Dictionary<string, object> GetSchema();

        /// <summary>
        /// Executes the tool with the given input (typically a JSON string).
        /// </summary>
        public abstract Task<string> ExecuteAsync(string input);

        /// <summary>
        /// Helper to generate OpenAI-compatible function schema.
        /// </summary>
        protected Dictionary<string, object> CreateFunctionSchema(
            string functionName,
            string description,
            Dictionary<string, object> parameters,
            List<string>? requiredParams = null)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object>
                    {
                        { "name", functionName },
                        { "description", description },
                        { "parameters", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", parameters },
                                { "required", requiredParams ?? new List<string>() }
                            }
                        }
                    }
                }
            };
            return schema;
        }

        /// <summary>
        /// Parse tool input from JSON string to strongly-typed object.
        /// </summary>
        protected T? ParseInput<T>(string jsonInput) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(jsonInput, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "Tool", $"Failed to parse tool input: {ex.Message}", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Serialize result to JSON string.
        /// </summary>
        protected string SerializeResult<T>(T result)
        {
            try
            {
                return JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "Tool", $"Failed to serialize tool result: {ex.Message}", ex.ToString());
                return "{}";
            }
        }
    }

    /// <summary>
    /// Interface for tools that track model/agent context (previously ITinySkill).
    /// </summary>
    public interface ILangChainToolWithContext
    {
        int? ModelId { get; set; }
        string? ModelName { get; set; }
        int? AgentId { get; set; }
    }

    /// <summary>
    /// Enhanced tool interface for tracking function calls.
    /// Extends ILangChainToolWithContext with call tracking for testing.
    /// </summary>
    public interface ITinyTool : ILangChainToolWithContext
    {
        /// <summary>
        /// Name of the last function that was called on this tool.
        /// Used for testing function call invocation.
        /// </summary>
        string? LastFunctionCalled { get; set; }

        /// <summary>
        /// Result of the last function that was called on this tool.
        /// Used for testing function call results.
        /// </summary>
        string? LastFunctionResult { get; set; }
    }
}
