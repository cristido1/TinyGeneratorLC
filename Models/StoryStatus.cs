namespace TinyGenerator.Models;

public sealed class StoryStatus
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public int Step { get; set; }
    public string? Color { get; set; }
    public string? OperationType { get; set; } // none | agent_call | function_call
    public string? AgentType { get; set; } // evaluator | writer | tts | music | fx | ambient | none
    public string? FunctionName { get; set; } // only for function_call
}