namespace TinyGenerator.Models;

public sealed class ModelRoleErrorGridRow
{
    public string ModelName { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string? Operation { get; set; }
    public long Id { get; set; }
    public int ParentId { get; set; }
    public string ErrorText { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public string? DateInsert { get; set; }
    public string? DateLast { get; set; }
}

