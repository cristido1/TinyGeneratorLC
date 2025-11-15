namespace TinyGenerator.Models;

public sealed class TestDefinition
{
    public int Id { get; set; }
    public string? GroupName { get; set; }
    public string? Library { get; set; }
    // Comma-separated list of allowed plugins/addins to register for this test (optional).
    // If present, this overrides/augments the Library field and is used to create kernels with only
    // the specified plugins registered (e.g. "text,math" ).
    public string? AllowedPlugins { get; set; }
    public string? FunctionName { get; set; }
    public string? Prompt { get; set; }
    public string? ExpectedBehavior { get; set; }
    public string? ExpectedAsset { get; set; }
    // New field: indicates how this test should be evaluated (e.g. 'functioncall')
    public string? TestType { get; set; }
    // Optional explicit expected prompt value used by some importer workflows
    public string? ExpectedPromptValue { get; set; }
    // Valid score range as provided by importer (e.g. "1-3")
    public string? ValidScoreRange { get; set; }
    public int TimeoutMs { get; set; }
    public int Priority { get; set; }
    public string? Description { get; set; }
    public string? ExecutionPlan { get; set; }
    // Active flag (soft delete / enable/disable)
    public bool Active { get; set; } = true;
}
