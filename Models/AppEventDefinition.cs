using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("app_events")]
    public class AppEventDefinition
    {
        [Key]
        [Column("id")]
        public long? Id { get; set; }

        [Column("event_type")]
        public string EventType { get; set; } = string.Empty;

        [Column("description")]
        public string? Description { get; set; }

        [Column("enabled")]
        public bool Enabled { get; set; }

        [Column("logged")]
        public bool Logged { get; set; }

        [Column("notified")]
        public bool Notified { get; set; }

        [Column("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [Column("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
