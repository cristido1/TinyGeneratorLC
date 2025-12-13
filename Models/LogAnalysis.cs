using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("log_analysis")]
    public class LogAnalysis
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("threadId")]
        public string ThreadId { get; set; } = string.Empty;

        [Column("model_id")]
        public string ModelId { get; set; } = string.Empty;

        [Column("run_scope")]
        public string RunScope { get; set; } = string.Empty;

        [Column("description")]
        public string Description { get; set; } = string.Empty;

        [Column("succeeded")]
        public bool Succeeded { get; set; }
    }
}
