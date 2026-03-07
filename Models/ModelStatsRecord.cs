using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("stats_models")]
    public partial class ModelStatsRecord : ISoftDelete, IActiveFlag, IOrderable
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

        [Column("duration_total_count")]
        public int? DurationTotalCount { get; set; }

        [Column("duration_total_time")]
        public double? DurationTotalTime { get; set; }

        [Column("runtime_total_count")]
        public int? RuntimeTotalCount { get; set; }

        [Column("prompt_eval_count_total")]
        public long? PromptEvalCountTotal { get; set; }

        [Column("prompt_eval_duration_total")]
        public double? PromptEvalDurationTotal { get; set; }

        [Column("eval_count_total")]
        public long? EvalCountTotal { get; set; }

        [Column("eval_duration_total")]
        public double? EvalDurationTotal { get; set; }

        [Column("total_duration_total")]
        public double? TotalDurationTotal { get; set; }

        [Column("load_duration_total")]
        public double? LoadDurationTotal { get; set; }

        [Column("done_stop_count")]
        public int? DoneStopCount { get; set; }

        [Column("done_length_count")]
        public int? DoneLengthCount { get; set; }

        [Column("done_other_count")]
        public int? DoneOtherCount { get; set; }
    }
}
