using JobParser.Helpers;
using JobParser.Models;
using JobParser.Repositories;
using JobParser.Services.Interfaces;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace JobParser.Services;

public class AmountWorkParser : ISiteParser
{
    private readonly ILogger<AmountWorkParser> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProgressRepository _progressRepo;
    private readonly ProcessedUrlsRepository _urlsRepo;
    private readonly HtmlParserService _htmlParser;

    private const string BaseUrl = "https://amountwork.com/ua/rabota/ssha/voditel";
    private const string Domain = "https://amountwork.com";
    public string SiteName => "AmountWork";

    public AmountWorkParser(
        ILogger<AmountWorkParser> logger,
        IConfiguration configuration,
        ProgressRepository progressRepo,
        ProcessedUrlsRepository urlsRepo,
        HtmlParserService htmlParser)
    {
        _logger = logger;
        _configuration = configuration;
        _progressRepo = progressRepo;
        _urlsRepo = urlsRepo;
        _htmlParser = htmlParser;
    }

    public async Task<List<JobLead>> ParseAsync()
    {
        _logger.LogInformation("{Site}: используется Selenium", SiteName);

        IWebDriver? driver = null;
        var leads = new List<JobLead>();

        try
        {
            driver = CreateSeleniumDriver();

            var maxPagesToScan = _configuration.GetValue<int>("ParserSettings:MaxPagesToScan", 10);
            var (startPage, totalPages) = await _progressRepo.GetScanRangeAsync(SiteName, maxPagesToScan);

            _logger.LogInformation("Начало парсинга {Site}. Страницы {Start}-{End}",
                SiteName, startPage, startPage + totalPages - 1);

            var jobUrls = await CollectJobUrlsAsync(driver, startPage, totalPages);

            _logger.LogInformation("Собрано URL: {Count}", jobUrls.Count);

            if (jobUrls.Count == 0)
            {
                _logger.LogWarning("{Site}: не найдено ссылок на объявления", SiteName);
                return leads;
            }

            var newUrls = await _urlsRepo.FilterNewUrlsAsync(jobUrls, SiteName);

            _logger.LogInformation("Найдено ссылок: {Total}, новых: {New}",
                jobUrls.Count, newUrls.Count);

            if (newUrls.Count == 0)
            {
                _logger.LogWarning("{Site}: все ссылки уже обработаны", SiteName);
                return leads;
            }

            var maxLeadsPerSite = _configuration.GetValue<int>("ParserSettings:MaxLeadsPerSite", 100);
            if (maxLeadsPerSite > 0 && newUrls.Count > maxLeadsPerSite)
            {
                _logger.LogInformation("Ограничение MaxLeadsPerSite: {Limit}, будет обработано: {Count}",
                    maxLeadsPerSite, maxLeadsPerSite);
                newUrls = newUrls.Take(maxLeadsPerSite).ToList();
            }

            leads = await ParseJobsAsync(driver, newUrls);

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

    private IWebDriver CreateSeleniumDriver()
    {
        var options = new ChromeOptions();

        options.AddArgument("--headless=new");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--window-size=1920,1080");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        options.AddArgument("--blink-settings=imagesEnabled=false");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);

        options.PageLoadStrategy = PageLoadStrategy.Eager;

        var driver = new ChromeDriver(options);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(15);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);

        return driver;
    }

    private async Task<List<string>> CollectJobUrlsAsync(IWebDriver driver, int startPage, int totalPages)
    {
        var urls = new HashSet<string>();
        var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 1000);

        for (int i = 0; i < totalPages; i++)
        {
            int page = startPage + i;
            var pageUrl = page == 1 ? BaseUrl : $"{BaseUrl}?page={page}";

            try
            {
                _logger.LogInformation("Сканирование страницы {Current}/{Total}: {Url}",
                    i + 1, totalPages, pageUrl);

                driver.Navigate().GoToUrl(pageUrl);
                await Task.Delay(1500);

                var html = driver.PageSource;

                if (string.IsNullOrEmpty(html) || html.Length < 1000)
                {
                    _logger.LogWarning("Страница {Page}: слишком короткий HTML, возможна блокировка", page);
                    continue;
                }

                var links = ParserHelper.ExtractJobUrls(
                    html,
                    Domain,
                    @"href=""(/ua/v/\d+/[^""]+)""");

                if (links.Count == 0)
                {
                    _logger.LogWarning("Страница {Page}: пустая, достигнут конец списка", page);
                    await _progressRepo.SaveProgressAsync(SiteName, 1);
                    break;
                }

                foreach (var link in links)
                {
                    urls.Add(link);
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
        await _progressRepo.SaveProgressAsync(SiteName, nextPage);
        _logger.LogInformation("Прогресс сохранен: следующий старт со страницы {Next}", nextPage);

        return urls.ToList();
    }

    private async Task<List<JobLead>> ParseJobsAsync(IWebDriver driver, List<string> urls)
    {
        var leads = new List<JobLead>();
        var delay = _configuration.GetValue<int>("ParserSettings:DelayBetweenRequests", 1000);

        foreach (var url in urls)
        {
            try
            {
                await Task.Delay(delay);

                driver.Navigate().GoToUrl(url);
                await Task.Delay(1000);

                var html = driver.PageSource;

                if (string.IsNullOrEmpty(html) || html.Length < 500)
                {
                    _logger.LogWarning("Пропуск {Url}: слишком короткий HTML", url);
                    await _urlsRepo.MarkAsProcessedAsync(url, SiteName);
                    continue;
                }

                var lead = _htmlParser.ParseJobPage(html, url, SiteName);

                if (lead != null)
                {
                    leads.Add(lead);
                    LogLead(leads.Count, lead);
                }

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

    private void LogLead(int count, JobLead lead)
    {
        _logger.LogInformation(
            "[{N}] {Title} | Phones: {Ph} | Email: {Em} | Loc: {Loc}",
            count,
            ParserHelper.Truncate(lead.Title, 50),
            lead.PhoneNumbers.Count,
            lead.Email ?? "-",
            lead.Location ?? "-");
    }
}