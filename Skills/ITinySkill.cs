using System;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// Common interface for all TinyGenerator skills.
    /// Provides shared properties for tracking skill execution context and metadata.
    /// </summary>
    public interface ITinySkill
    {
        /// <summary>
        /// Model ID associated with this skill instance (nullable).
        /// </summary>
        int? ModelId { get; set; }

        /// <summary>
        /// Model name associated with this skill instance (nullable).
        /// </summary>
        string? ModelName { get; set; }

        /// <summary>
        /// Agent ID associated with this skill instance (nullable).
        /// </summary>
        int? AgentId { get; }

        /// <summary>
        /// Agent name associated with this skill instance (nullable).
        /// </summary>
        string? AgentName { get; }

        /// <summary>
        /// Timestamp of the last time this skill was called.
        /// </summary>
        DateTime? LastCalled { get; set; }

        /// <summary>
        /// Name of the last function/kernel function that was called on this skill.
        /// </summary>
        string? LastFunction { get; set; }

        /// <summary>
        /// Logger for tracking skill function invocations in the Live Monitor
        /// </summary>
        ICustomLogger? Logger { get; set; }

        /// <summary>
        /// Logs a function call to the Live Monitor. Format: "[AgentName/Model] called function (FunctionName) [details]"
        /// </summary>
        /// <param name="functionName">Name of the function being called</param>
        /// <param name="details">Optional details to include in the log (e.g., parameter values)</param>
        void LogFunctionCall(string functionName, string? details = null)
        {
            if (Logger == null) return;
            var agentLabel = !string.IsNullOrEmpty(AgentName) ? AgentName : ModelName ?? "Unknown";
            var message = !string.IsNullOrEmpty(details) 
                ? $"[{agentLabel}] called function ({functionName}) [{details}]"
                : $"[{agentLabel}] called function ({functionName})";
            Logger.Log("Information", "SkillFunction", message);
        }
    }
}
