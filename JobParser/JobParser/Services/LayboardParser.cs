using JobParser.Helpers;
using JobParser.Models;
using JobParser.Repositories;
using JobParser.Services.Interfaces;

namespace JobParser.Services;

public class LayboardParser : ISiteParser
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LayboardParser> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProgressRepository _progressRepo;
    private readonly ProcessedUrlsRepository _urlsRepo;
    private readonly HtmlParserService _htmlParser;

    private const string BaseUrl = "https://layboard.com/ua/vakansii/ssha";
    private const string Domain = "https://layboard.com";
    public string SiteName => "Layboard";

    public LayboardParser(
        HttpClient httpClient,
        ILogger<LayboardParser> logger,
        IConfiguration configuration,
        ProgressRepository progressRepo,
        ProcessedUrlsRepository urlsRepo,
        HtmlParserService htmlParser)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _progressRepo = progressRepo;
        _urlsRepo = urlsRepo;
        _htmlParser = htmlParser;
    }

    public async Task<List<JobLead>> ParseAsync()
    {
        var leads = new List<JobLead>();
        var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 1000);
        var maxLeadsPerSite = _configuration.GetValue<int>("ParserSettings:MaxLeadsPerSite", 100);

        try
        {
            var categoriesPerRun = _configuration.GetValue<int>("ParserSettings:CategoriesPerRun", 5);
            var (startCategoryIndex, _) = await _progressRepo.GetScanRangeAsync(SiteName, categoriesPerRun);

            _logger.LogInformation("Начало парсинга {Site}. Категории {Start}-{End}",
                SiteName, startCategoryIndex, startCategoryIndex + categoriesPerRun - 1);

            var allCategories = await GetCategoryUrlsAsync();

            if (allCategories.Count == 0)
            {
                _logger.LogWarning("Категории не найдены");
                return leads;
            }

            _logger.LogInformation("Найдено категорий всего: {Count}", allCategories.Count);

            var categories = allCategories
                .Skip(startCategoryIndex)
                .Take(categoriesPerRun)
                .ToList();

            _logger.LogInformation("Обрабатываем {Count} категорий из {Total}",
                categories.Count, allCategories.Count);

            var jobUrls = await CollectJobUrlsFromCategoriesAsync(categories, delay, maxLeadsPerSite);

            _logger.LogInformation("Всего объявлений собрано: {Count}", jobUrls.Count);

            var newUrls = await _urlsRepo.FilterNewUrlsAsync(jobUrls, SiteName);

            _logger.LogInformation("Всего объявлений: {Total}, новых: {New}",
                jobUrls.Count, newUrls.Count);

            if (newUrls.Count == 0)
            {
                _logger.LogWarning("{Site}: все объявления уже обработаны", SiteName);

                var nextIndex = startCategoryIndex + categoriesPerRun;
                if (nextIndex >= allCategories.Count)
                    nextIndex = 0;

                await _progressRepo.SaveProgressAsync(SiteName, nextIndex);
                return leads;
            }

            if (maxLeadsPerSite > 0 && newUrls.Count > maxLeadsPerSite)
            {
                _logger.LogInformation("Ограничение MaxLeadsPerSite: {Limit}, будет обработано: {Count}",
                    maxLeadsPerSite, maxLeadsPerSite);
                newUrls = newUrls.Take(maxLeadsPerSite).ToList();
            }

            leads = await ParseJobsAsync(newUrls, delay, maxLeadsPerSite);

            var nextCategoryIndex = startCategoryIndex + categoriesPerRun;
            if (nextCategoryIndex >= allCategories.Count)
            {
                _logger.LogInformation("Достигнут конец категорий, начинаем сначала");
                nextCategoryIndex = 0;
            }

            await _progressRepo.SaveProgressAsync(SiteName, nextCategoryIndex);
            _logger.LogInformation("Завершено {Site}. Лидов: {Count}", SiteName, leads.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка {Site}", SiteName);
        }

        return leads;
    }

    private async Task<List<string>> GetCategoryUrlsAsync()
    {
        try
        {
            var html = await _httpClient.GetStringAsync(BaseUrl);
            var categories = ParserHelper.ExtractJobUrls(
                html,
                Domain,
                @"href=""(/ua/vakansii/ssha/speciality-[^""]+)""");

            _logger.LogDebug("Извлечено категорий: {Count}", categories.Count);
            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка получения категорий");
            return new List<string>();
        }
    }

    private async Task<List<string>> CollectJobUrlsFromCategoriesAsync(
        List<string> categories,
        int delay,
        int maxNeeded)
    {
        var urls = new HashSet<string>();
        var maxPagesToScan = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 10);

        foreach (var categoryUrl in categories)
        {
            if (maxNeeded > 0 && urls.Count >= maxNeeded)
            {
                _logger.LogInformation("Достигнут лимит URL: {Limit}", maxNeeded);
                break;
            }

            var needed = maxNeeded > 0 ? maxNeeded - urls.Count : int.MaxValue;

            for (int page = 1; page <= maxPagesToScan; page++)
            {
                if (urls.Count >= needed)
                    break;

                var pageUrl = page == 1 ? categoryUrl : $"{categoryUrl}?page={page}";

                try
                {
                    var html = await _httpClient.GetStringAsync(pageUrl);
                    var links = ParserHelper.ExtractJobUrls(
                        html,
                        Domain,
                        @"href=""(/ua/vakansiya/\d+/[^""]+)""");

                    if (links.Count == 0)
                    {
                        _logger.LogDebug("Категория {Url} страница {Page}: пустая", categoryUrl, page);
                        break;
                    }

                    foreach (var link in links)
                    {
                        urls.Add(link);
                        if (urls.Count >= needed)
                            break;
                    }

                    _logger.LogDebug("Категория страница {Page}: +{Count} ссылок", page, links.Count);

                    if (page < maxPagesToScan && urls.Count < needed)
                        await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка страницы категории: {Url}", pageUrl);
                    break;
                }
            }

            _logger.LogInformation("Категория {Url}: {Count} ссылок", categoryUrl, urls.Count);

            await Task.Delay(delay);
        }

        return urls.ToList();
    }

    private async Task<List<JobLead>> ParseJobsAsync(List<string> urls, int delay, int maxLeads)
    {
        var leads = new List<JobLead>();

        foreach (var url in urls)
        {
            if (maxLeads > 0 && leads.Count >= maxLeads)
            {
                _logger.LogInformation("Достигнут лимит лидов: {Limit}", maxLeads);
                break;
            }

            try
            {
                await Task.Delay(delay);

                var html = await _httpClient.GetStringAsync(url);
                var lead = _htmlParser.ParseJobPage(html, url, SiteName);

                if (lead != null)
                {
                    leads.Add(lead);

                    _logger.LogInformation(
                        "[{N}] {Title} | Phones: {Ph} | Email: {Em} | Loc: {Loc}",
                        leads.Count,
                        ParserHelper.Truncate(lead.Title, 50),
                        lead.PhoneNumbers.Count,
                        lead.Email ?? "-",
                        lead.Location ?? "-");
                }

                await _urlsRepo.MarkAsProcessedAsync(url, SiteName);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("404: {Url}", url);
                await _urlsRepo.MarkAsProcessedAsync(url, SiteName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                await _urlsRepo.MarkAsProcessedAsync(url, SiteName);
            }
        }

        return leads;
    }
}