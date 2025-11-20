using System;

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
        int? ModelId { get; }

        /// <summary>
        /// Model name associated with this skill instance (nullable).
        /// </summary>
        string? ModelName { get; }

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
    }
}
