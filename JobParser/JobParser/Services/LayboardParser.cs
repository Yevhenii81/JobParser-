using HtmlAgilityPack;
using JobParser.Data;
using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace JobParser.Services
{
    public class LayboardParser : ISiteParser
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<LayboardParser> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private const string BaseUrl = "https://layboard.com/ua/vakansii/ssha";
        private const string Domain = "https://layboard.com";
        public string SiteName => "Layboard";

        public LayboardParser(
           HttpClient httpClient,
            ILogger<LayboardParser> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
        }

        public async Task<List<JobLead>> ParseAsync()
        {
            var leads = new List<JobLead>();
            var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 2000);
            var maxLeads = _configuration.GetValue<int>("ParserSettings:MaxLeadsPerSite", 0);

            try
            {
                var (startCategoryIndex, categoriesPerRun) = await GetCategoryScanRangeAsync();

                _logger.LogInformation("Начало парсинга {Site}. Категории {Start}-{End}",
                    SiteName, startCategoryIndex, startCategoryIndex + categoriesPerRun - 1);

                var allCategoryUrls = await GetCategoryUrlsAsync();
                _logger.LogInformation("Найдено категорий всего: {Count}", allCategoryUrls.Count);

                if (allCategoryUrls.Count == 0)
                {
                    _logger.LogWarning("Категорий не найдено");
                    return leads;
                }

                var categoryUrls = allCategoryUrls
                    .Skip(startCategoryIndex)
                    .Take(categoriesPerRun)
                    .ToList();

                _logger.LogInformation("Обрабатываем {Count} категорий из {Total}",
                    categoryUrls.Count, allCategoryUrls.Count);

                var jobUrls = new HashSet<string>();

                foreach (var categoryUrl in categoryUrls)
                {
                    if (maxLeads > 0 && jobUrls.Count >= maxLeads)
                        break;

                    var needed = maxLeads > 0 ? maxLeads - jobUrls.Count : int.MaxValue;
                    var urls = await GetJobUrlsFromCategoryAsync(categoryUrl, delay, needed);

                    foreach (var url in urls)
                    {
                        jobUrls.Add(url);
                        if (maxLeads > 0 && jobUrls.Count >= maxLeads)
                            break;
                    }
                    await Task.Delay(delay);
                }

                _logger.LogInformation("Всего объявлений собрано: {Count}", jobUrls.Count);

                var newUrls = await FilterProcessedUrlsAsync(jobUrls.ToList());

                _logger.LogInformation("Всего объявлений: {Total}, новых: {New}",
                    jobUrls.Count, newUrls.Count);

                if (newUrls.Count == 0)
                {
                    _logger.LogWarning("{Site}: все объявления уже обработаны", SiteName);
                    await SaveCategoryProgressAsync(startCategoryIndex + categoriesPerRun, allCategoryUrls.Count);
                    return leads;
                }

                foreach (var url in newUrls)
                {
                    if (maxLeads > 0 && leads.Count >= maxLeads)
                        break;

                    try
                    {
                        await Task.Delay(delay);
                        var lead = await ParseJobPageAsync(url);

                        if (lead != null)
                        {
                            leads.Add(lead);
                            await MarkUrlAsProcessedAsync(url);

                            _logger.LogInformation(
                                "[{N}] {Title} | Phones: {Ph} | Email: {Em} | Loc: {Loc}",
                                leads.Count,
                                Truncate(lead.Title, 50),
                                lead.PhoneNumbers.Count,
                                lead.Email ?? "-",
                                lead.Location ?? "-");
                        }
                        else
                        {
                            await MarkUrlAsProcessedAsync(url);
                        }
                    }
                    catch (HttpRequestException ex)
                        when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _logger.LogWarning("404: {Url}", url);
                        await MarkUrlAsProcessedAsync(url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                        await MarkUrlAsProcessedAsync(url);
                    }
                }

                await SaveCategoryProgressAsync(startCategoryIndex + categoriesPerRun, allCategoryUrls.Count);
                _logger.LogInformation("Завершен {Site}. Лидов: {Count}", SiteName, leads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка {Site}", SiteName);
            }

            return leads;
        }

        private async Task<(int startIndex, int count)> GetCategoryScanRangeAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var progress = await context.ParserProgress
                    .FirstOrDefaultAsync(p => p.Source == SiteName);

                int categoriesPerRun = _configuration.GetValue<int>("ParserSettings:CategoriesPerRun", 5);

                if (progress == null)
                {
                    return (0, categoriesPerRun);
                }

                var daysSinceUpdate = (DateTime.UtcNow - progress.UpdatedAt).TotalDays;
                if (daysSinceUpdate > 7)
                {
                    _logger.LogInformation("Данные устарели ({Days} дней), начинаем сначала", (int)daysSinceUpdate);
                    return (0, categoriesPerRun);
                }

                return (progress.LastProcessedPage, categoriesPerRun);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения прогресса, начинаем с первой категории");
                return (0, 5);
            }
        }

        private async Task SaveCategoryProgressAsync(int nextCategoryIndex, int totalCategories)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var progress = await context.ParserProgress
                    .FirstOrDefaultAsync(p => p.Source == SiteName);

                int nextIndex = nextCategoryIndex >= totalCategories ? 0 : nextCategoryIndex;

                if (progress == null)
                {
                    context.ParserProgress.Add(new ParserProgress
                    {
                        Source = SiteName,
                        LastProcessedPage = nextIndex,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    progress.LastProcessedPage = nextIndex;
                    progress.UpdatedAt = DateTime.UtcNow;
                }

                await context.SaveChangesAsync();
                _logger.LogDebug("Прогресс сохранён: следующая категория {Index}", nextIndex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить прогресс");
            }
        }

        private async Task<List<string>> FilterProcessedUrlsAsync(List<string> urls)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var processedUrls = await context.ProcessedLeads
                    .Where(p => p.Source == SiteName)
                    .Select(p => p.Url)
                    .ToListAsync();

                var newUrls = urls.Where(url => !processedUrls.Contains(url)).ToList();

                _logger.LogInformation("Обработано ранее: {Processed}, новых: {New}",
                    processedUrls.Count, newUrls.Count);

                return newUrls;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка фильтрации URL, возвращаем все");
                return urls;
            }
        }

        private async Task MarkUrlAsProcessedAsync(string url)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var exists = await context.ProcessedLeads.AnyAsync(p => p.Url == url);

                if (!exists)
                {
                    context.ProcessedLeads.Add(new ProcessedLead
                    {
                        Url = url,
                        Source = SiteName,
                        ProcessedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync();
                    _logger.LogDebug("URL сохранён как обработанный: {Url}", url);
                }
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key") == true)
            {
                _logger.LogDebug("URL уже в БД: {Url}", url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить обработанный URL: {Url}", url);
            }
        }

        private async Task<List<string>> GetCategoryUrlsAsync()
        {
            var categories = new List<string>();
            try
            {
                var html = await _httpClient.GetStringAsync(BaseUrl);
                var links = Regex
                    .Matches(html, @"href=""(/ua/vakansii/ssha/speciality-[^""]+)""")
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .ToList();

                foreach (var href in links)
                    categories.Add(Domain + href);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения категорий");
            }
            return categories;
        }

        private async Task<List<string>> GetJobUrlsFromCategoryAsync(
            string categoryUrl, int delay, int maxNeeded)
        {
            var urls = new HashSet<string>();
            int maxPagesPerCategory = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 10);

            try
            {
                for (int page = 1; page <= maxPagesPerCategory; page++)
                {
                    if (urls.Count >= maxNeeded)
                        break;

                    var pageUrl = page == 1
                        ? categoryUrl
                        : $"{categoryUrl}?page={page}";

                    var html = await _httpClient.GetStringAsync(pageUrl);
                    var links = Regex
                        .Matches(html, @"href=""(/ua/vakansiya/\d+/[^""]+)""")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();

                    if (links.Count == 0)
                        break;

                    foreach (var href in links)
                    {
                        urls.Add(Domain + href);
                        if (urls.Count >= maxNeeded)
                            break;
                    }

                    if (page < maxPagesPerCategory && urls.Count < maxNeeded)
                        await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка категории: {Url}", categoryUrl);
            }

            _logger.LogInformation("Категория {Url}: {Count} ссылок", categoryUrl, urls.Count);
            return urls.ToList();
        }

        private async Task<JobLead?> ParseJobPageAsync(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var title = doc.DocumentNode
                    .SelectSingleNode("//h1")
                    ?.InnerText.Trim() ?? string.Empty;

                title = System.Net.WebUtility.HtmlDecode(title);
                title = Regex.Replace(title, @"\s+", " ").Trim();

                var descNode = doc.DocumentNode.SelectSingleNode(
                    "//div[contains(@class,'vacancy__description')] | " +
                    "//div[contains(@class,'vacancy-description')] | " +
                    "//div[contains(@class,'description')] | " +
                    "//div[contains(@class,'vacancy-text')]");

                var description = string.Empty;
                if (descNode != null)
                {
                    description = System.Net.WebUtility.HtmlDecode(descNode.InnerText);
                    description = Regex.Replace(description, @"\s+", " ").Trim();
                    if (description.Length > 1500)
                        description = description[..1500] + "...";
                }

                var fullText = doc.DocumentNode.InnerText;
                var phones = PhoneExtractor.ExtractPhones(fullText);

                string? email = null;
                var emailMatch = Regex.Match(fullText,
                    @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
                    RegexOptions.IgnoreCase);
                if (emailMatch.Success)
                    email = emailMatch.Value.ToLower();

                if (email == null)
                {
                    var mailtoMatch = Regex.Match(html,
                        @"mailto:([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})",
                        RegexOptions.IgnoreCase);
                    if (mailtoMatch.Success)
                        email = mailtoMatch.Groups[1].Value.ToLower();
                }

                if (phones.Count == 0 && email == null)
                {
                    _logger.LogDebug("Нет контактов: {Url}", url);
                    return null;
                }

                string? location = null;

                var locNode = doc.DocumentNode.SelectSingleNode(
                    "//*[contains(@class,'vacancy__location')] | " +
                    "//*[contains(@class,'location')] | " +
                    "//span[contains(@class,'city')]");

                if (locNode != null)
                {
                    location = System.Net.WebUtility.HtmlDecode(locNode.InnerText).Trim();
                    location = Regex.Replace(location, @"\s+", " ").Trim();
                }

                if (string.IsNullOrEmpty(location))
                {
                    var usaCity = Regex.Match(html,
                        @"США\s*\(([^)]{2,50})\)", RegexOptions.IgnoreCase);
                    if (usaCity.Success)
                        location = usaCity.Groups[1].Value.Trim();
                }

                if (string.IsNullOrEmpty(location))
                {
                    var cities = new[]
                    {
                        "Нью-Йорк", "Лос-Анджелес", "Чикаго", "Майами", "Акрон",
                        "Лас-Вегас", "Денвер", "Бостон", "Сан-Франциско", "Хьюстон",
                        "New York", "Los Angeles", "Chicago", "Miami", "Houston"
                    };
                    foreach (var city in cities)
                    {
                        if (fullText.Contains(city, StringComparison.OrdinalIgnoreCase))
                        {
                            location = city;
                            break;
                        }
                    }
                }

                return new JobLead
                {
                    Title = title,
                    Description = description,
                    PhoneNumbers = phones,
                    Email = email,
                    Location = location,
                    Source = SiteName,
                    ParsedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                return null;
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }
    }
}