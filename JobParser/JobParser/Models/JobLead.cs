namespace JobParser.Models
{
    public class JobLead
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> PhoneNumbers { get; set; } = new();
        public string? Email { get; set; }
        public string? Location { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Region { get; set; }
        public DateTime ParsedAt { get; set; } = DateTime.UtcNow;
    }
}