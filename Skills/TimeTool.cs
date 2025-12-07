using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for time-related functions.
    /// Converted from TimeSkill (Semantic Kernel).
    /// </summary>
    public class TimeTool : BaseLangChainTool, ITinyTool
    {
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public TimeTool(ICustomLogger? logger = null) 
            : base("time", "Time operations", logger)
        {
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
                            { "description", "Operation" }
                        }
                    },
                    {
                        "days",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Days to add" }
                        }
                    },
                    {
                        "hours",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Hours to add" }
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
                var request = ParseInput<TimeToolRequest>(input);
                if (request == null)
                    return Task.FromResult(SerializeResult(new { error = "Invalid input format" }));

                CustomLogger?.Log("Info", "TimeTool", $"Executing operation: {request.Operation}");

                var result = request.Operation?.ToLowerInvariant() switch
                {
                    "now" => SerializeResult(new { result = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }),
                    "today" => SerializeResult(new { result = DateTime.Now.ToString("yyyy-MM-dd") }),
                    "adddays" => SerializeResult(new { result = DateTime.Now.AddDays(request.Days ?? 0).ToString("yyyy-MM-dd") }),
                    "addhours" => SerializeResult(new { result = DateTime.Now.AddHours(request.Hours ?? 0).ToString("yyyy-MM-dd HH:mm:ss") }),
                    "describe" => SerializeResult(new { result = "Available operations: now(), today(), adddays(days), addhours(hours). Example: adddays(5) returns a date 5 days in the future." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TimeTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return Task.FromResult(SerializeResult(new { error = ex.Message }));
            }
        }

        private class TimeToolRequest
        {
            public string? Operation { get; set; }
            public int? Days { get; set; }
            public int? Hours { get; set; }
        }
    }
}
