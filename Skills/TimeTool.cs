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
            : base("time", "Provides time-related functions such as getting the current date and time, and date arithmetic.", logger)
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
                            { "description", "The time operation: 'now', 'today', 'adddays', 'addhours', 'describe'" }
                        }
                    },
                    {
                        "days",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Number of days to add (for adddays operation)" }
                        }
                    },
                    {
                        "hours",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Number of hours to add (for addhours operation)" }
                        }
                    }
                },
                new List<string> { "operation" }
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<TimeToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "TimeTool", $"Executing operation: {request.Operation}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "now" => SerializeResult(new { result = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }),
                    "today" => SerializeResult(new { result = DateTime.Now.ToString("yyyy-MM-dd") }),
                    "adddays" => SerializeResult(new { result = DateTime.Now.AddDays(request.Days ?? 0).ToString("yyyy-MM-dd") }),
                    "addhours" => SerializeResult(new { result = DateTime.Now.AddHours(request.Hours ?? 0).ToString("yyyy-MM-dd HH:mm:ss") }),
                    "describe" => SerializeResult(new { result = "Available operations: now(), today(), adddays(days), addhours(hours). Example: adddays(5) returns a date 5 days in the future." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TimeTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
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
