using System;

namespace TinyGenerator.Models;

public sealed class ModelRoleErrorEntry
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string ErrorText { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public string? DateInsert { get; set; }
    public string? DateLast { get; set; }
}
