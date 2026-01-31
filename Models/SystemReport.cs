using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("system_reports")]
    public class SystemReport
    {
        [Key]
        [Column("id")]
        public long Id { get; set; }

        [Column("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [Column("severity")]
        public string Severity { get; set; } = "info";

        [Column("status")]
        public string Status { get; set; } = "new";

        [Column("deleted")]
        public bool Deleted { get; set; }

        [Column("deleted_at")]
        public string? DeletedAt { get; set; }

        [Column("deleted_by")]
        public string? DeletedBy { get; set; }

        [Column("title")]
        public string? Title { get; set; }

        [Column("message")]
        public string? Message { get; set; }

        [Column("failure_reason")]
        public string? FailureReason { get; set; }

        [Column("agent_name")]
        public string? AgentName { get; set; }

        [Column("agent_role")]
        public string? AgentRole { get; set; }

        [Column("model_name")]
        public string? ModelName { get; set; }

        [Column("story_id")]
        public long? StoryId { get; set; }

        [Column("series_id")]
        public int? SeriesId { get; set; }

        [Column("series_episode")]
        public int? SeriesEpisode { get; set; }

        [Column("operation_type")]
        public string? OperationType { get; set; }

        [Column("execution_time_ms")]
        public int? ExecutionTimeMs { get; set; }

        [Column("retry_count")]
        public int? RetryCount { get; set; }

        [Column("raw_log_ref")]
        public string? RawLogRef { get; set; }
    }
}
