using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("app_events")]
    public partial class AppEventDefinition : ISoftDelete, IActiveFlag, IOrderable, IEntity
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }
        [Column("event_type")]
        public string EventType { get; set; } = string.Empty;

        [Column("description")]
        public string? Description { get; set; }

        [NotMapped]
        public bool Enabled
        {
            get => IsActive;
            set => IsActive = value;
        }

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




