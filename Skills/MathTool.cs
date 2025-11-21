using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain version of MathSkill.
    /// Provides basic arithmetic operations as a LangChain Tool.
    /// </summary>
    public class MathTool : BaseLangChainTool, ILangChainToolWithContext
    {
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }

        public MathTool(ICustomLogger? logger = null) 
            : base("math", "Arithmetic operations: add, subtract, multiply, divide", logger)
        {
        }

        public override Dictionary<string, object> GetSchema()
        {
            var properties = new Dictionary<string, object>
            {
                { "operation", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "enum", new List<string> { "add", "subtract", "multiply", "divide" } },
                        { "description", "The math operation: add, subtract, multiply, or divide" }
                    }
                },
                { "a", new Dictionary<string, object>
                    {
                        { "type", "number" },
                        { "description", "First number" }
                    }
                },
                { "b", new Dictionary<string, object>
                    {
                        { "type", "number" },
                        { "description", "Second number" }
                    }
                }
            };

            return CreateFunctionSchema("math", "Arithmetic operations", properties, new List<string> { "operation", "a", "b" });
        }

        public override async Task<string> ExecuteAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<MathToolInput>(jsonInput, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                double result = 0;
                string operation = input.Operation?.ToLower() ?? "";

                if (operation == "add")
                    result = input.A + input.B;
                else if (operation == "subtract")
                    result = input.A - input.B;
                else if (operation == "multiply")
                    result = input.A * input.B;
                else if (operation == "divide")
                {
                    if (input.B == 0)
                        return JsonSerializer.Serialize(new { error = "Division by zero not allowed" });
                    result = input.A / input.B;
                }
                else
                    return JsonSerializer.Serialize(new { error = $"Unknown operation: {operation}" });

                CustomLogger?.Log("Info", "MathTool", $"Executed {operation}({input.A}, {input.B}) = {result}");
                return await Task.FromResult(JsonSerializer.Serialize(new { result = result }));
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "MathTool", $"Execution failed: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    public class MathToolInput
    {
        [JsonPropertyName("operation")]
        public string? Operation { get; set; }

        [JsonPropertyName("a")]
        public double A { get; set; }

        [JsonPropertyName("b")]
        public double B { get; set; }
    }
}
