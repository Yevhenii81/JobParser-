using JobParser.Models;

namespace JobParser.Services.Interfaces
{
    public interface ISiteParser
    {
        string SiteName { get; }
        Task<List<JobLead>> ParseAsync();
    }
}