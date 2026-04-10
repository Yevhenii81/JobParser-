using HtmlAgilityPack;
using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;
using System.Text.RegularExpressions;

namespace JobParser.Services
{
    public class AmountWorkParser : ISiteParser
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AmountWorkParser> _logger;
        private readonly IConfiguration _configuration;

        private const string BaseUrl = "https://amountwork.com/ua/rabota/ssha/voditel";
        private const string Domain = "https://amountwork.com";

        public string SiteName => "AmountWork";

        public AmountWorkParser(
             HttpClient httpClient,
             ILogger<AmountWorkParser> logger,
             IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "uk-UA,uk;q=0.9,en-US;q=0.8,en;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        }

        public async Task<List<JobLead>> ParseAsync()
        {
            var leads = new List<JobLead>();
            var maxPages = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 5);
            var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 2000);

            try
            {
                _logger.LogInformation("Начало парсинга {Site}", SiteName);

                var jobUrls = await CollectJobUrlsAsync(maxPages, delay);
                _logger.LogInformation("Найдено ссылок: {Count}", jobUrls.Count);

                if (jobUrls.Count == 0)
                {
                    _logger.LogWarning("AmountWork: ссылки не найдены. Возможно сайт заблокировал запрос или изменилась структура.");
                    return leads;
                }

                foreach (var url in jobUrls)
                {
                    try
                    {
                        await Task.Delay(delay);
                        var lead = await ParseJobPageAsync(url);
                        if (lead != null)
                            leads.Add(lead);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка парсинга страницы: {Url}", url);
                    }
                }

                _logger.LogInformation(
                    "Завершен парсинг {Site}. Найдено {Count} объявлений",
                    SiteName, leads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга сайта {Site}", SiteName);
            }

            return leads;
        }

        private async Task<List<string>> CollectJobUrlsAsync(int maxPages, int delay)
        {
            var urls = new HashSet<string>();

            for (int page = 1; page <= maxPages; page++)
            {
                try
                {
                    var pageUrl = page == 1
                        ? BaseUrl
                        : $"{BaseUrl}?page={page}";

                    _logger.LogInformation("Сканирование страницы {Page}: {Url}", page, pageUrl);

                    var response = await _httpClient.GetAsync(pageUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("AmountWork вернул {Status} на странице {Page}",
                            (int)response.StatusCode, page);
                        break;
                    }

                    var html = await response.Content.ReadAsStringAsync();

                    var links = Regex
                        .Matches(html, @"href=""(/ua/v/\d+/[^""]+)""")
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .ToList();

                    if (links.Count == 0)
                    {
                        _logger.LogWarning("Страница {Page}: объявления не найдены, остановка", page);
                        break;
                    }

                    foreach (var href in links)
                    {
                        urls.Add(Domain + href);
                    }

                    _logger.LogInformation("Страница {Page}: +{New} объявлений (всего: {Total})",
                        page, links.Count, urls.Count);

                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка на странице {Page}", page);
                    break;
                }
            }

            return urls.ToList();
        }

        private async Task<JobLead?> ParseJobPageAsync(string url)
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var title = doc.DocumentNode
                .SelectSingleNode("//h1")
                ?.InnerText.Trim() ?? string.Empty;

            var description = doc.DocumentNode
                .SelectSingleNode(
                    "//div[contains(@class,'description')] | " +
                    "//div[contains(@class,'vacancy-body')] | " +
                    "//div[contains(@class,'job-content')] | " +
                    "//article")
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
                    "//*[contains(@class,'address')]")
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