namespace JobParser.Models
{
    public class ParserProgress
    {
        public int Id { get; set; }
        public string Source { get; set; } = string.Empty;
        public int LastProcessedPage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}