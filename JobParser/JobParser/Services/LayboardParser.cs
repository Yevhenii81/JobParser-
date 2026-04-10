using HtmlAgilityPack;
using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;
using System.Text.RegularExpressions;

namespace JobParser.Services
{
    public class LayboardParser : ISiteParser
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<LayboardParser> _logger;
        private readonly IConfiguration _configuration;

        private const string BaseUrl = "https://layboard.com/ua/vakansii/ssha";
        private const string Domain = "https://layboard.com";

        public string SiteName => "Layboard";

        public LayboardParser(
            HttpClient httpClient,
            ILogger<LayboardParser> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<List<JobLead>> ParseAsync()
        {
            var leads = new List<JobLead>();
            var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 500);
            var maxLeadsPerSite = _configuration.GetValue<int>("ParserSettings:MaxLeadsPerSite", 10);
            var maxPagesToScan = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 1);

            try
            {
                _logger.LogInformation("Начало парсинга {Site}", SiteName);
                _logger.LogInformation("Лимиты: MaxLeads={MaxLeads}, MaxPages={MaxPages}",
                    maxLeadsPerSite, maxPagesToScan);

                var categoryUrls = await GetCategoryUrlsAsync();
                _logger.LogInformation("Найдено категорий: {Count}", categoryUrls.Count);

                var maxCategories = 2;
                var categoriesToProcess = categoryUrls.Take(maxCategories).ToList();

                if (categoryUrls.Count > maxCategories)
                {
                    _logger.LogInformation("Ограничение до {Max} категорий (всего: {Total})",
                        maxCategories, categoryUrls.Count);
                }

                var jobUrls = new HashSet<string>();

                foreach (var categoryUrl in categoriesToProcess)
                {
                    if (jobUrls.Count >= maxLeadsPerSite)
                    {
                        _logger.LogInformation("Достигнут лимит ссылок ({Max})", maxLeadsPerSite);
                        break;
                    }

                    var urls = await GetJobUrlsFromCategoryAsync(
                        categoryUrl,
                        delay,
                        maxPagesToScan,
                        maxLeadsPerSite - jobUrls.Count);

                    foreach (var url in urls)
                    {
                        jobUrls.Add(url);
                        if (jobUrls.Count >= maxLeadsPerSite)
                            break;
                    }

                    await Task.Delay(delay);
                }

                _logger.LogInformation("Найдено объявлений для парсинга: {Count}", jobUrls.Count);

                foreach (var url in jobUrls)
                {
                    if (leads.Count >= maxLeadsPerSite)
                    {
                        _logger.LogInformation("Достигнут лимит лидов ({Max})", maxLeadsPerSite);
                        break;
                    }

                    try
                    {
                        await Task.Delay(delay);
                        var lead = await ParseJobPageAsync(url);

                        if (lead != null)
                        {
                            leads.Add(lead);
                            _logger.LogInformation("Лид {Current}/{Max}: {Title}",
                                leads.Count, maxLeadsPerSite, lead.Title);
                        }
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _logger.LogWarning("Страница не найдена (404): {Url}", url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                    }
                }

                _logger.LogInformation("Завершено {Site}. Лидов: {Count}", SiteName, leads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга {Site}", SiteName);
            }

            return leads;
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

                _logger.LogInformation("Категорий найдено: {Count}", categories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения категорий");
            }
            return categories;
        }

        private async Task<List<string>> GetJobUrlsFromCategoryAsync(
            string categoryUrl,
            int delay,
            int maxPagesToScan,
            int maxUrlsNeeded)
        {
            var urls = new HashSet<string>();

            try
            {
                _logger.LogInformation("Сканирование категории: {Url} (макс {Max} страниц)",
                    categoryUrl, maxPagesToScan);

                for (int page = 1; page <= maxPagesToScan; page++)
                {
                    if (urls.Count >= maxUrlsNeeded)
                    {
                        _logger.LogInformation("Достаточно ссылок ({Count})", urls.Count);
                        break;
                    }

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
                    {
                        _logger.LogInformation("Страница {Page} пустая, остановка", page);
                        break;
                    }

                    foreach (var href in links)
                    {
                        urls.Add(Domain + href);

                        if (urls.Count >= maxUrlsNeeded)
                            break;
                    }

                    _logger.LogInformation("Страница {Page}: найдено {Count} ссылок",
                        page, links.Count);

                    if (page < maxPagesToScan && urls.Count < maxUrlsNeeded)
                        await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка категории: {Url}", categoryUrl);
            }

            _logger.LogInformation("Категория {Url}: собрано {Count} ссылок",
                categoryUrl, urls.Count);

            return urls.ToList();
        }

        private async Task<JobLead?> ParseJobPageAsync(string url)
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var title = doc.DocumentNode
                .SelectSingleNode("//h1")
                ?.InnerText.Trim() ?? string.Empty;

            var description = doc.DocumentNode
                .SelectSingleNode(
                    "//div[contains(@class,'vacancy__description')] | " +
                    "//div[contains(@class,'description')] | " +
                    "//div[contains(@class,'vacancy-text')] | " +
                    "//div[contains(@class,'content')]")
                ?.InnerText.Trim() ?? string.Empty;

            var fullText = doc.DocumentNode.InnerText;
            var phones = PhoneExtractor.ExtractPhones(fullText);

            var emailMatch = Regex.Match(fullText,
                @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b");

            if (phones.Count == 0 && !emailMatch.Success)
            {
                _logger.LogDebug("Нет контактов: {Url}", url);
                return null;
            }

            var location = doc.DocumentNode
                .SelectSingleNode(
                    "//*[contains(@class,'location')] | " +
                    "//*[contains(@class,'city')] | " +
                    "//*[contains(@class,'country')]")
                ?.InnerText.Trim();

            return new JobLead
            {
                Title = title,
                Description = description,
                PhoneNumbers = phones,
                Email = emailMatch.Success ? emailMatch.Value : null,
                Location = location,
                Source = SiteName
            };
        }
    }
}