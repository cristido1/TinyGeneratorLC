using System;
using System.Collections.Generic;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("task_executions")]
    public class TaskExecution
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("task_type")]
        public string TaskType { get; set; } = string.Empty;
        
        [Column("entity_id")]
        public long? EntityId { get; set; }
        
        [Column("step_prompt")]
        public string StepPrompt { get; set; } = string.Empty;
        
        [Column("initial_context")]
        public string? InitialContext { get; set; } // User theme/context for the task
        
        [Column("current_step")]
        public int CurrentStep { get; set; } = 1;
        
        [Column("max_step")]
        public int MaxStep { get; set; }
        
        [Column("retry_count")]
        public int RetryCount { get; set; } = 0;
        
        [Column("status")]
        public string Status { get; set; } = "pending"; // pending, in_progress, completed, failed, paused
        
        [Column("executor_agent_id")]
        public int? ExecutorAgentId { get; set; }
        
        [Column("checker_agent_id")]
        public int? CheckerAgentId { get; set; }
        
        [Column("config")]
        public string? Config { get; set; }
        
        [Column("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        
        [Column("updated_at")]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        // Non-persistent properties
        [NotMapped]
        public List<TaskExecutionStep> Steps { get; set; } = new List<TaskExecutionStep>();
        
        [NotMapped]
        public TaskTypeInfo? TypeInfo { get; set; }
        
        [NotMapped]
        public string? ExecutorAgentName { get; set; }
        
        [NotMapped]
        public string? CheckerAgentName { get; set; }
    }

    [Table("task_execution_steps")]
    public class TaskExecutionStep
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("execution_id")]
        public long ExecutionId { get; set; }
        
        [Column("step_number")]
        public int StepNumber { get; set; }
        
        [Column("step_instruction")]
        public string StepInstruction { get; set; } = string.Empty;
        
        [Column("step_output")]
        public string? StepOutput { get; set; }
        
        [Column("validation_result")]
        public string? ValidationResultJson { get; set; }
        
        [Column("attempt_count")]
        public int AttemptCount { get; set; } = 1;
        
        [Column("started_at")]
        public string? StartedAt { get; set; }
        
        [Column("completed_at")]
        public string? CompletedAt { get; set; }

        // Non-persistent property - deserialize from ValidationResultJson
        [NotMapped]
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

    [Table("task_types")]
    public class TaskTypeInfo
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("code")]
        public string Code { get; set; } = string.Empty;
        
        [Column("description")]
        public string? Description { get; set; }
        
        [Column("default_executor_role")]
        public string DefaultExecutorRole { get; set; } = string.Empty;
        
        [Column("default_checker_role")]
        public string DefaultCheckerRole { get; set; } = string.Empty;
        
        [Column("output_merge_strategy")]
        public string OutputMergeStrategy { get; set; } = string.Empty;
        
        [Column("validation_criteria")]
        public string? ValidationCriteria { get; set; }

        // Non-persistent property - deserialize from ValidationCriteria JSON
        [NotMapped]
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

    [Table("step_templates")]
    public class StepTemplate
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("name")]
        public string Name { get; set; } = string.Empty;
        
        [Column("task_type")]
        public string TaskType { get; set; } = string.Empty;
        
        [Column("step_prompt")]
        public string StepPrompt { get; set; } = string.Empty;
        
        [Column("instructions")]
        public string? Instructions { get; set; }
        
        [Column("description")]
        public string? Description { get; set; }
        
        /// <summary>
        /// Step number that generates the character list (e.g., 2 means step 2 output contains characters).
        /// If null, no automatic character extraction is performed.
        /// </summary>
        [Column("characters_step")]
        public int? CharactersStep { get; set; }
        
        /// <summary>
        /// Comma-separated list of step numbers that require evaluator validation.
        /// E.g., "3,5,7" means steps 3, 5, and 7 will be evaluated by all active evaluators.
        /// If average score is below 6, the step is retried with evaluator feedback.
        /// </summary>
        [Column("evaluation_steps")]
        public string? EvaluationSteps { get; set; }

        [Column("trama_steps")]
        public string? TramaSteps { get; set; }
        
        [Column("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        
        [Column("updated_at")]
        public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
        
        /// <summary>
        /// Parses the EvaluationSteps field into a list of step numbers.
        /// </summary>
        [NotMapped]
        public List<int> ParsedEvaluationSteps
        {
            get
            {
                if (string.IsNullOrWhiteSpace(EvaluationSteps))
                    return new List<int>();
                
                return EvaluationSteps
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0)
                    .Where(n => n > 0)
                    .ToList();
            }
        }

        [NotMapped]
        public List<int> ParsedTramaSteps
        {
            get
            {
                if (string.IsNullOrWhiteSpace(TramaSteps))
                    return new List<int>();

                return TramaSteps
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0)
                    .Where(n => n > 0)
                    .ToList();
            }
        }
    }
}
