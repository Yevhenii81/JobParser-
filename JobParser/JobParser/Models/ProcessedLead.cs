namespace JobParser.Models
{
    public class ProcessedLead
    {
        public int Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }
}