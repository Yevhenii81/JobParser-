using HtmlAgilityPack;
using JobParser.Data;
using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Text.RegularExpressions;

namespace JobParser.Services
{
    public class AmountWorkParser : ISiteParser
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AmountWorkParser> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private const string BaseUrl = "https://amountwork.com/ua/rabota/ssha/voditel";
        private const string Domain = "https://amountwork.com";
        public string SiteName => "AmountWork";
        private readonly bool _useSelenium;

        public AmountWorkParser(
            HttpClient httpClient,
            ILogger<AmountWorkParser> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider)
        {
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _useSelenium = _configuration.GetValue<bool>("ParserSettings:UseSeleniumForAmountWork", true);
        }

        public async Task<List<JobLead>> ParseAsync()
        {
            if (_useSelenium)
            {
                _logger.LogInformation("{Site}: используется Selenium", SiteName);
                return await ParseWithSeleniumAsync();
            }
            else
            {
                _logger.LogInformation("{Site}: используется HttpClient", SiteName);
                return await ParseWithHttpClientAsync();
            }
        }

        #region Selenium метод

        private async Task<List<JobLead>> ParseWithSeleniumAsync()
        {
            var leads = new List<JobLead>();
            var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 2000);

            ChromeOptions options = CreateOptimizedChromeOptions();
            IWebDriver? driver = null;

            try
            {
                driver = new ChromeDriver(options);
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(15);
                driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);

                var (startPage, totalPages) = await GetScanRangeAsync();

                _logger.LogInformation("Начало парсинга {Site}. Страницы {Start}-{End}",
                    SiteName, startPage, startPage + totalPages - 1);

                var jobUrls = await CollectJobUrlsWithSeleniumAsync(driver, startPage, totalPages, delay);
                var newUrls = await FilterProcessedUrlsAsync(jobUrls);

                _logger.LogInformation("Найдено ссылок: {Total}, новых: {New}",
                    jobUrls.Count, newUrls.Count);

                if (newUrls.Count == 0)
                {
                    _logger.LogWarning("{Site}: все ссылки уже обработаны", SiteName);
                    return leads;
                }

                foreach (var url in newUrls)
                {
                    try
                    {
                        await Task.Delay(delay);
                        var lead = await ParseJobPageWithSeleniumAsync(driver, url);

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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                        await MarkUrlAsProcessedAsync(url);
                    }
                }

                _logger.LogInformation("Завершено {Site}. Лидов: {Count}", SiteName, leads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка {Site}", SiteName);
            }
            finally
            {
                try
                {
                    driver?.Quit();
                    driver?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ошибка закрытия Selenium");
                }
            }

            return leads;
        }

        private async Task<(int startPage, int totalPages)> GetScanRangeAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var progress = await context.ParserProgress
                    .FirstOrDefaultAsync(p => p.Source == SiteName);

                int pagesPerRun = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 10);

                if (progress == null)
                {
                    return (1, pagesPerRun);
                }

                var daysSinceUpdate = (DateTime.UtcNow - progress.UpdatedAt).TotalDays;
                if (daysSinceUpdate > 7)
                {
                    _logger.LogInformation("Данные устарели ({Days} дней), начинаем сначала", (int)daysSinceUpdate);
                    return (1, pagesPerRun);
                }

                return (progress.LastProcessedPage, pagesPerRun);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения прогресса, начинаем с первой страницы");
                return (1, 10);
            }
        }

        private async Task SaveProgressAsync(int nextPage)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var progress = await context.ParserProgress
                    .FirstOrDefaultAsync(p => p.Source == SiteName);

                if (progress == null)
                {
                    context.ParserProgress.Add(new ParserProgress
                    {
                        Source = SiteName,
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
                _logger.LogDebug("Прогресс сохранён: следующая страница {Page}", nextPage);
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

        private ChromeOptions CreateOptimizedChromeOptions()
        {
            var options = new ChromeOptions();

            options.AddArgument("--headless=new");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--disable-images");
            options.AddArgument("--blink-settings=imagesEnabled=false");
            options.AddArgument("--disable-extensions");
            options.AddArgument("--disable-plugins");
            options.AddArgument("--disable-popup-blocking");
            options.AddArgument("--disable-notifications");
            options.AddArgument("--disable-logging");
            options.AddArgument("--disable-web-security");
            options.AddArgument("--ignore-certificate-errors");
            options.AddArgument("--disable-blink-features=AutomationControlled");
            options.AddArgument("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            options.AddExcludedArgument("enable-automation");
            options.AddAdditionalOption("useAutomationExtension", false);
            options.PageLoadStrategy = PageLoadStrategy.Eager;

            return options;
        }

        private async Task<List<string>> CollectJobUrlsWithSeleniumAsync(IWebDriver driver, int startPage, int totalPages, int delay)
        {
            var urls = new HashSet<string>();

            for (int i = 0; i < totalPages; i++)
            {
                int page = startPage + i;

                try
                {
                    var pageUrl = page == 1 ? BaseUrl : $"{BaseUrl}?page={page}";
                    _logger.LogInformation("Сканирование страницы {Current}/{Total}: {Url}",
                        i + 1, totalPages, pageUrl);

                    driver.Navigate().GoToUrl(pageUrl);
                    await Task.Delay(1500);

                    var html = driver.PageSource;
                    var links = Regex
                        .Matches(html, @"href=""(/ua/v/\d+/[^""]+)""")
                        .Select(m => m.Groups[1].Value)
                        .Where(h => !h.Contains("?"))
                        .Distinct()
                        .ToList();

                    if (links.Count == 0)
                    {
                        _logger.LogWarning("Страница {Page}: пустая, достигнут конец списка", page);
                        await SaveProgressAsync(1);
                        break;
                    }

                    foreach (var href in links)
                    {
                        urls.Add(Domain + href);
                    }

                    _logger.LogInformation("Страница {Page}: +{New} ссылок (всего: {Total})",
                        page, links.Count, urls.Count);

                    if (i < totalPages - 1)
                        await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка на странице {Page}", page);
                    break;
                }
            }

            int nextPage = startPage + totalPages;
            await SaveProgressAsync(nextPage);
            _logger.LogInformation("Прогресс сохранён: следующий старт со страницы {Next}", nextPage);

            return urls.ToList();
        }

        private async Task<JobLead?> ParseJobPageWithSeleniumAsync(IWebDriver driver, string url)
        {
            try
            {
                driver.Navigate().GoToUrl(url);
                await Task.Delay(1000);

                var html = driver.PageSource;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var title = string.Empty;
                try
                {
                    var titleNode = doc.DocumentNode.SelectSingleNode("//h1");
                    if (titleNode != null)
                    {
                        title = titleNode.InnerText.Trim();
                    }
                    else
                    {
                        title = driver.FindElement(By.TagName("h1")).Text.Trim();
                    }
                }
                catch
                {
                    title = "Без заголовка";
                }

                title = System.Net.WebUtility.HtmlDecode(title);
                title = Regex.Replace(title, @"\s+", " ").Trim();

                var descNode = doc.DocumentNode.SelectSingleNode(
                    "//div[contains(@class,'vacancy-description')] | " +
                    "//div[contains(@class,'job-description')] | " +
                    "//div[contains(@class,'description')] | " +
                    "//div[contains(@class,'vacancy-body')]");

                var description = string.Empty;
                if (descNode != null)
                {
                    description = System.Net.WebUtility.HtmlDecode(descNode.InnerText);
                    description = Regex.Replace(description, @"\s+", " ").Trim();
                    if (description.Length > 1000)
                        description = description[..1000] + "...";
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
                    "//*[contains(@class,'vacancy-location')] | " +
                    "//*[contains(@class,'job-location')] | " +
                    "//*[contains(@class,'location')] | " +
                    "//*[contains(@class,'city')]");

                if (locNode != null)
                {
                    location = System.Net.WebUtility.HtmlDecode(locNode.InnerText).Trim();
                    location = Regex.Replace(location, @"\s+", " ").Trim();

                    var cityInParens = Regex.Match(location, @"США\s*\(([^)]+)\)");
                    if (cityInParens.Success)
                        location = cityInParens.Groups[1].Value.Trim();
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

        #endregion

        #region HttpClient метод

        private async Task<List<JobLead>> ParseWithHttpClientAsync()
        {
            var leads = new List<JobLead>();
            var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 2000);

            try
            {
                var (startPage, totalPages) = await GetScanRangeAsync();

                _logger.LogInformation("Начало парсинга {Site}. Страницы {Start}-{End}",
                    SiteName, startPage, startPage + totalPages - 1);

                await GetInitialCookiesAsync();

                var jobUrls = await CollectJobUrlsAsync(startPage, totalPages, delay);
                var newUrls = await FilterProcessedUrlsAsync(jobUrls);

                _logger.LogInformation("Найдено ссылок: {Total}, новых: {New}",
                    jobUrls.Count, newUrls.Count);

                if (newUrls.Count == 0)
                {
                    _logger.LogWarning("{Site}: все ссылки уже обработаны", SiteName);
                    return leads;
                }

                foreach (var url in newUrls)
                {
                    try
                    {
                        await Task.Delay(delay);
                        var lead = await ParseJobPageAsync(url);

                        if (lead != null)
                        {
                            leads.Add(lead);
                            await MarkUrlAsProcessedAsync(url);

                            _logger.LogInformation(
                                "[{N}] {Title} | Phones: {Ph} | Email: {Em}",
                                leads.Count,
                                Truncate(lead.Title, 50),
                                lead.PhoneNumbers.Count,
                                lead.Email ?? "-");
                        }
                        else
                        {
                            await MarkUrlAsProcessedAsync(url);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка парсинга: {Url}", url);
                        await MarkUrlAsProcessedAsync(url);
                    }
                }

                _logger.LogInformation("Завершено {Site}. Лидов: {Count}", SiteName, leads.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка {Site}", SiteName);
            }

            return leads;
        }

        private async Task GetInitialCookiesAsync()
        {
            try
            {
                _logger.LogInformation("Получение cookies с {Domain}...", Domain);
                var response = await _httpClient.GetAsync(Domain);
                _logger.LogInformation("Главная страница: {StatusCode}", response.StatusCode);
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось получить cookies");
            }
        }

        private async Task<List<string>> CollectJobUrlsAsync(int startPage, int totalPages, int delay)
        {
            var urls = new HashSet<string>();

            for (int i = 0; i < totalPages; i++)
            {
                int page = startPage + i;

                try
                {
                    var pageUrl = page == 1 ? BaseUrl : $"{BaseUrl}?page={page}";
                    _logger.LogInformation("Сканирование страницы {Current}/{Total}: {Url}",
                        i + 1, totalPages, pageUrl);

                    var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    request.Headers.Add("Referer", Domain + "/");
                    var response = await _httpClient.SendAsync(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("{Site} вернул {Status} на странице {Page}",
                            SiteName, (int)response.StatusCode, page);
                        break;
                    }

                    var html = await response.Content.ReadAsStringAsync();
                    var links = Regex
                        .Matches(html, @"href=""(/ua/v/\d+/[^""]+)""")
                        .Select(m => m.Groups[1].Value)
                        .Where(h => !h.Contains("?"))
                        .Distinct()
                        .ToList();

                    if (links.Count == 0)
                    {
                        _logger.LogWarning("Страница {Page}: пустая, достигнут конец списка", page);
                        await SaveProgressAsync(1);
                        break;
                    }

                    foreach (var href in links)
                    {
                        urls.Add(Domain + href);
                    }

                    _logger.LogInformation("Страница {Page}: +{New} ссылок (всего: {Total})",
                        page, links.Count, urls.Count);

                    if (i < totalPages - 1)
                        await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка на странице {Page}", page);
                    break;
                }
            }

            int nextPage = startPage + totalPages;
            await SaveProgressAsync(nextPage);
            _logger.LogInformation("Прогресс сохранён: следующий старт со страницы {Next}", nextPage);

            return urls.ToList();
        }

        private async Task<JobLead?> ParseJobPageAsync(string url)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Referer", BaseUrl);

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var title = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? string.Empty;
                title = System.Net.WebUtility.HtmlDecode(title);
                title = Regex.Replace(title, @"\s+", " ").Trim();

                var fullText = doc.DocumentNode.InnerText;
                var phones = PhoneExtractor.ExtractPhones(fullText);

                string? email = null;
                var emailMatch = Regex.Match(fullText,
                    @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
                    RegexOptions.IgnoreCase);

                if (emailMatch.Success)
                    email = emailMatch.Value.ToLower();

                if (phones.Count == 0 && email == null)
                    return null;

                return new JobLead
                {
                    Title = title,
                    Description = "",
                    PhoneNumbers = phones,
                    Email = email,
                    Location = null,
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
        #endregion
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }
    }
}