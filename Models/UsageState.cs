using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("usage_state")]
public class UsageState
{
    public int Id { get; set; }
    [Column("month")]
    public string Month { get; set; } = string.Empty;
    [Column("tokens_this_run")]
    public long TokensThisRun { get; set; }
    [Column("tokens_this_month")]
    public long TokensThisMonth { get; set; }
    [Column("cost_this_month")]
    public double CostThisMonth { get; set; }
}
