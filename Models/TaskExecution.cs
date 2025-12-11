using System;
using System.Collections.Generic;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;

namespace TinyGenerator.Models
{
    public class TaskExecution
    {
        public long Id { get; set; }
        public string TaskType { get; set; } = string.Empty;
        public long? EntityId { get; set; }
        public string StepPrompt { get; set; } = string.Empty;
        public string? InitialContext { get; set; } // User theme/context for the task
        public int CurrentStep { get; set; } = 1;
        public int MaxStep { get; set; }
        public int RetryCount { get; set; } = 0;
        public string Status { get; set; } = "pending"; // pending, in_progress, completed, failed, paused
        public int? ExecutorAgentId { get; set; }
        public int? CheckerAgentId { get; set; }
        public string? Config { get; set; }
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        // Non-persistent properties
        public List<TaskExecutionStep> Steps { get; set; } = new List<TaskExecutionStep>();
        public TaskTypeInfo? TypeInfo { get; set; }
        public string? ExecutorAgentName { get; set; }
        public string? CheckerAgentName { get; set; }
    }

    public class TaskExecutionStep
    {
        public long Id { get; set; }
        public long ExecutionId { get; set; }
        public int StepNumber { get; set; }
        public string StepInstruction { get; set; } = string.Empty;
        public string? StepOutput { get; set; }
        public string? ValidationResultJson { get; set; }
        public int AttemptCount { get; set; } = 1;
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }

        // Non-persistent property - deserialize from ValidationResultJson
        public ValidationResult? ParsedValidation
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ValidationResultJson)) return null;
                try
                {
                    return JsonSerializer.Deserialize<ValidationResult>(ValidationResultJson);
                }
                catch
                {
                    return null;
                }
            }
            set
            {
                if (value == null)
                {
                    ValidationResultJson = null;
                }
                else
                {
                    ValidationResultJson = JsonSerializer.Serialize(value);
                }
            }
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool NeedsRetry { get; set; }
        public double? SemanticScore { get; set; }
        public Dictionary<string, object>? ValidationDetails { get; set; }
        // Optional: if set, this message should be injected as a system message on the next retry
        // instead of being included inside the user prompt/context.
        public string? SystemMessageOverride { get; set; }
    }

    public class TaskTypeInfo
    {
        public long Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string DefaultExecutorRole { get; set; } = string.Empty;
        public string DefaultCheckerRole { get; set; } = string.Empty;
        public string OutputMergeStrategy { get; set; } = string.Empty;
        public string? ValidationCriteria { get; set; }

        // Non-persistent property - deserialize from ValidationCriteria JSON
        public Dictionary<string, object>? ParsedValidationCriteria
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ValidationCriteria)) return null;
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(ValidationCriteria);
                }
                catch
                {
                    return null;
                }
            }
        }
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }

    public class StepTemplate
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string TaskType { get; set; } = string.Empty;
        public string StepPrompt { get; set; } = string.Empty;
        public string? Instructions { get; set; }
        public string? Description { get; set; }
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
