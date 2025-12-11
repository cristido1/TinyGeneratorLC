using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("test_definitions")]
public sealed class TestDefinition
{
    [Column("id")]
    public int Id { get; set; }
    
    [Column("test_group")]
    public string? GroupName { get; set; }
    
    [Column("library")]
    public string? Library { get; set; }
    
    // Comma-separated list of allowed plugins/addins to register for this test (optional).
    // If present, this overrides/augments the Library field and is used to create kernels with only
    // the specified plugins registered (e.g. "text,math" ).
    [Column("allowed_plugins")]
    public string? AllowedPlugins { get; set; }
    
    [Column("function_name")]
    public string? FunctionName { get; set; }
    
    [Column("prompt")]
    public string? Prompt { get; set; }
    
    [Column("expected_behavior")]
    public string? ExpectedBehavior { get; set; }
    
    [Column("expected_asset")]
    public string? ExpectedAsset { get; set; }
    
    // New field: indicates how this test should be evaluated (e.g. 'functioncall')
    [Column("test_type")]
    public string? TestType { get; set; }
    
    // Optional explicit expected prompt value used by some importer workflows
    [Column("expected_prompt_value")]
    public string? ExpectedPromptValue { get; set; }
    
    // Valid score range as provided by importer (e.g. "1-3")
    [Column("valid_score_range")]
    public string? ValidScoreRange { get; set; }
    
    [Column("timeout_secs")]
    public int TimeoutSecs { get; set; }
    
    [Column("priority")]
    public int Priority { get; set; }
    
    [NotMapped]
    public string? Description { get; set; }
    
    [Column("execution_plan")]
    public string? ExecutionPlan { get; set; }
    
    // Active flag (soft delete / enable/disable)
    [Column("active")]
    public bool Active { get; set; } = true;
    
    // Optional: filename under response_formats/ that defines expected JSON response schema
    [Column("json_response_format")]
    public string? JsonResponseFormat { get; set; }
    
    // Optional: comma-separated list of files from test_source_files/ to copy to test folder
    [Column("files_to_copy")]
    public string? FilesToCopy { get; set; }
    
    // Sampling parameters for model calls
    [Column("temperature")]
    public double? Temperature { get; set; }
    
    [Column("top_p")]
    public double? TopP { get; set; }
}
