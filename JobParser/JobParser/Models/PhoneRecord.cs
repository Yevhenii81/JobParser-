using System.ComponentModel.DataAnnotations;

namespace JobParser.Models
{
    public class PhoneRecord
    {
        public int Id { get; set; }
        [Required]
        [MaxLength(20)]
        public string Number { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}