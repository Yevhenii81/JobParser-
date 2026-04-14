using JobParser.Data;
using JobParser.Models;
using Microsoft.EntityFrameworkCore;

namespace JobParser.Repositories;

public class ProcessedUrlsRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessedUrlsRepository> _logger;

    public ProcessedUrlsRepository(IServiceProvider serviceProvider, ILogger<ProcessedUrlsRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<List<string>> FilterNewUrlsAsync(List<string> urls, string source)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var processedUrls = await context.ProcessedLeads
                .Where(p => p.Source == source && urls.Contains(p.Url))
                .Select(p => p.Url)
                .ToListAsync();

            var newUrls = urls.Except(processedUrls).ToList();

            _logger.LogInformation("Обработано ранее: {Old}, новых: {New}",
                processedUrls.Count, newUrls.Count);

            return newUrls;
        }
        catch
        {
            return urls;
        }
    }

    public async Task MarkAsProcessedAsync(string url, string source)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await context.ProcessedLeads.AnyAsync(p => p.Url == url))
                return;

            context.ProcessedLeads.Add(new ProcessedLead
            {
                Url = url,
                Source = source,
                ProcessedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
        {
        }
    }
}