using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("stats_models")]
    public class ModelStatsRecord
    {
        [Column("model_name")]
        public string ModelName { get; set; } = string.Empty;

        [Column("operation")]
        public string Operation { get; set; } = string.Empty;

        [Column("count_used")]
        public int? CountUsed { get; set; }

        [Column("count_successed")]
        public int? CountSuccessed { get; set; }

        [Column("count_failed")]
        public int? CountFailed { get; set; }

        [Column("total_success_time_secs")]
        public double? TotalSuccessTimeSecs { get; set; }

        [Column("total_fail_time_secs")]
        public double? TotalFailTimeSecs { get; set; }

        [Column("last_operation_date")]
        public string? LastOperationDate { get; set; }

        [Column("first_operation_date")]
        public string? FirstOperationDate { get; set; }
    }
}
