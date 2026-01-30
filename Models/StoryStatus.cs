using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("stories_status")]
public sealed class StoryStatus
{
    [Column("id")]
    public int Id { get; set; }
    
    [Column("code")]
    public string? Code { get; set; }
    
    [Column("description")]
    public string? Description { get; set; }
    
    [Column("step")]
    public int Step { get; set; }
    
    [Column("color")]
    public string? Color { get; set; }
    
    [Column("operation_type")]
    public string? OperationType { get; set; } // none | agent_call | function_call
    
    [Column("agent_type")]
    public string? AgentType { get; set; } // evaluator | writer | tts | music | fx | ambient | none
    
    [Column("function_name")]
    public string? FunctionName { get; set; } // only for function_call
    
    [Column("caption_to_execute")]
    public string? CaptionToExecute { get; set; } // caption to execute for this status

    [Column("delete_next_items")]
    public bool DeleteNextItems { get; set; }
    
    // Concurrency token for optimistic locking
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
