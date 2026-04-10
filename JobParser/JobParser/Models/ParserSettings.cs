namespace JobParser.Models
{
    public class ParserSettings
    {
        public string PhoneCheckApiUrl { get; set; } = string.Empty;
        public string ExclusionsFilePath { get; set; } = "exclusions.txt";
        public string OutputFolder { get; set; } = "output";
        public int MaxPagesToScan { get; set; } = 5;
        public int DelayBetweenRequests { get; set; } = 2000;
    }
}