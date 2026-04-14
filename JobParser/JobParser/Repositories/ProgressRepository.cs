using JobParser.Data;
using JobParser.Models;
using Microsoft.EntityFrameworkCore;

namespace JobParser.Repositories;

public class ProgressRepository
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProgressRepository> _logger;

    public ProgressRepository(IServiceProvider serviceProvider, ILogger<ProgressRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<(int startPage, int totalPages)> GetScanRangeAsync(string source, int defaultPages)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var progress = await context.ParserProgress
                .FirstOrDefaultAsync(p => p.Source == source);

            if (progress == null)
                return (1, defaultPages);

            if ((DateTime.UtcNow - progress.UpdatedAt).TotalDays > 7)
            {
                _logger.LogInformation("Данные устарели, начинаем сначала");
                return (1, defaultPages);
            }

            return (progress.LastProcessedPage, defaultPages);
        }
        catch
        {
            return (1, defaultPages);
        }
    }

    public async Task SaveProgressAsync(string source, int nextPage)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var progress = await context.ParserProgress
                .FirstOrDefaultAsync(p => p.Source == source);

            if (progress == null)
            {
                context.ParserProgress.Add(new ParserProgress
                {
                    Source = source,
                    LastProcessedPage = nextPage,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                progress.LastProcessedPage = nextPage;
                progress.UpdatedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить прогресс");
        }
    }
}