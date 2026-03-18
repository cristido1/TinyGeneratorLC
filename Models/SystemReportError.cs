using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("system_reports_errors")]
public sealed class SystemReportError : IEntity, IActiveFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("error_type")]
    public string ErrorType { get; set; } = "unknown";

    [MaxLength(100)]
    [Column("agent")]
    public string? Agent { get; set; }

    [MaxLength(100)]
    [Column("step")]
    public string? Step { get; set; }

    [MaxLength(150)]
    [Column("check_name")]
    public string? CheckName { get; set; }

    [Column("fail_reason")]
    public string? FailReason { get; set; }

    [Column("error_summary")]
    public string? ErrorSummary { get; set; }

    [Column("occurrences")]
    public int Occurrences { get; set; }

    [Column("first_seen")]
    public DateTime FirstSeen { get; set; }

    [Column("last_seen")]
    public DateTime LastSeen { get; set; }

    [Required]
    [MaxLength(20)]
    [Column("status")]
    public string Status { get; set; } = "new";

    [Column("github_issue_id")]
    public int? GitHubIssueId { get; set; }

    [MaxLength(50)]
    [Column("fix_applied_version")]
    public string? FixAppliedVersion { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
