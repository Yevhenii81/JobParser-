namespace JobParser.Models
{
    public class ParserSettings
    {
        public string ExclusionsFilePath { get; set; } = "exclusions.txt";
        public string OutputFolder { get; set; } = "out";
        public int MaxPagesToScan { get; set; } = 10;
        public int DelayBetweenRequests { get; set; } = 2000;
        public int MaxLeadsPerSite { get; set; } = 0;
        public bool UseExternalPhoneApi { get; set; } = false;
        public string? PhoneCheckApiUrl { get; set; }
    }
}