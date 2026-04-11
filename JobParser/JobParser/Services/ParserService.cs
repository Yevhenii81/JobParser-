using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;
using System.Diagnostics;

namespace JobParser.Services
{
    public class ParserService
    {
        private readonly IEnumerable<ISiteParser> _parsers;
        private readonly PhoneCheckerService _phoneChecker;
        private readonly CsvExportService _csvExport;
        private readonly ExclusionFilter _exclusionFilter;
        private readonly ILogger<ParserService> _logger;
        private readonly IConfiguration _configuration;
        private readonly int _maxLeadsTotal;
        private readonly Stopwatch _stopwatch = new();

        public ParserService(
            IEnumerable<ISiteParser> parsers,
            PhoneCheckerService phoneChecker,
            CsvExportService csvExport,
            ExclusionFilter exclusionFilter,
            ILogger<ParserService> logger,
            IConfiguration configuration)
        {
            _parsers = parsers;
            _phoneChecker = phoneChecker;
            _csvExport = csvExport;
            _exclusionFilter = exclusionFilter;
            _logger = logger;
            _configuration = configuration;
            _maxLeadsTotal = _configuration.GetValue<int>("ParserSettings:MaxLeadsPerSite", 0);
        }

        public async Task RunAsync()
        {
            _logger.LogInformation("Запуск парсинга");
            _stopwatch.Start();

            var allLeads = new List<JobLead>();
            var regionStats = new Dictionary<string, int>();
            var sourceStats = new Dictionary<string, int>();
            var stats = new ParseStats();

            try
            {
                foreach (var parser in _parsers)
                {
                    _logger.LogInformation("Парсер: {Parser}", parser.SiteName);

                    var leads = await parser.ParseAsync();
                    allLeads.AddRange(leads);

                    sourceStats[parser.SiteName] = leads.Count;

                    _logger.LogInformation("Получено с {Parser}: {Count}", parser.SiteName, leads.Count);
                }

                stats.Total = allLeads.Count;
                _logger.LogInformation("Всего объявлений: {Count}", allLeads.Count);

                var validLeads = new List<JobLead>();

                foreach (var lead in allLeads)
                {
                    if (_maxLeadsTotal > 0 && validLeads.Count >= _maxLeadsTotal)
                    {
                        _logger.LogWarning("Достигнут лимит лидов: {Count}", _maxLeadsTotal);
                        break;
                    }

                    var result = await ProcessLeadAsync(lead, stats, regionStats);

                    if (result.isValid)
                    {
                        validLeads.Add(result.lead!);

                        _logger.LogInformation(
                            "#{Index} {Title} | Phones: {Phones} | Email: {Email} | Location: {Location} | Source: {Source}",
                            validLeads.Count,
                            Truncate(lead.Title, 50),
                            lead.PhoneNumbers.Count,
                            lead.Email ?? "-",
                            lead.Location ?? "-",
                            lead.Source
                        );
                    }
                }

                stats.Saved = validLeads.Count;
                _stopwatch.Stop();
                LogStatistics(stats, regionStats, sourceStats);
                await ExportLeadsAsync(validLeads);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка при парсинге");
                throw;
            }
            _logger.LogInformation("Парсинг завершен");
        }
        private async Task<(bool isValid, JobLead? lead)> ProcessLeadAsync(
            JobLead lead,
            ParseStats stats,
            Dictionary<string, int> regionStats)
        {
            try
            {
                if (_exclusionFilter.ContainsExclusions(lead.Title) ||
                    _exclusionFilter.ContainsExclusions(lead.Description))
                {
                    stats.ExcludedByFilter++;
                    return (false, null);
                }

                if (lead.PhoneNumbers.Count == 0)
                {
                    if (!string.IsNullOrEmpty(lead.Email))
                        return (true, lead);

                    stats.InvalidPhones++;
                    return (false, null);
                }

                var validPhones = new List<string>();

                foreach (var phone in lead.PhoneNumbers)
                {
                    var normalized = _phoneChecker.ValidateAndNormalize(phone);

                    if (normalized != null)
                    {
                        validPhones.Add(normalized);

                        var region = _phoneChecker.GetRegion(normalized) ?? "Unknown";
                        regionStats[region] = regionStats.GetValueOrDefault(region, 0) + 1;
                    }
                }

                if (validPhones.Count == 0)
                {
                    stats.InvalidPhones++;
                    return (false, null);
                }

                validPhones = validPhones.Distinct().ToList();
                lead.PhoneNumbers = validPhones;
                lead.Region = _phoneChecker.GetRegion(validPhones.First());

                bool exists = await _phoneChecker.AnyPhoneExistsAsync(validPhones);

                if (exists)
                {
                    stats.Duplicates++;
                    return (false, null);
                }
                return (true, lead);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки лида: {Title}", lead.Title);
                return (false, null);
            }
        }
        private void LogStatistics(ParseStats stats, Dictionary<string, int> regionStats, Dictionary<string, int> sourceStats)
        {
            _logger.LogInformation("Итоги парсинга:");
            _logger.LogInformation(
                "Всего: {Total} | Сохранено: {Saved} | Дубликаты: {Duplicates} | Невалидные: {Invalid} | Отфильтровано: {Filtered} | Время: {Time}",
                stats.Total,
                stats.Saved,
                stats.Duplicates,
                stats.InvalidPhones,
                stats.ExcludedByFilter,
                _stopwatch.Elapsed.ToString(@"mm\:ss")
            );

            if (sourceStats.Count > 0)
            {
                _logger.LogInformation("По источникам:");
                foreach (var (source, count) in sourceStats.OrderByDescending(x => x.Value))
                {
                    _logger.LogInformation("{Source}: {Count}", source, count);
                }
            }

            if (regionStats.Count > 0)
            {
                _logger.LogInformation("По регионам:");
                foreach (var (region, count) in regionStats.OrderByDescending(x => x.Value).Take(10))
                {
                    _logger.LogInformation("{Region}: {Count}", region, count);
                }
            }
        }
        private async Task ExportLeadsAsync(List<JobLead> validLeads)
        {
            if (validLeads.Count == 0)
            {
                _logger.LogWarning("Нет лидов для экспорта");
                return;
            }

            try
            {
                var file = await _csvExport.ExportToCsvAsync(validLeads);

                if (file != null)
                {
                    var count = _csvExport.GetRecordCount(file);
                    _logger.LogInformation("Сохранено в файл: {File} ({Count} записей)", file, count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка экспорта CSV");
            }
        }
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value[..maxLength] + "...";
        }
        private class ParseStats
        {
            public int Total { get; set; }
            public int InvalidPhones { get; set; }
            public int ExcludedByFilter { get; set; }
            public int Duplicates { get; set; }
            public int Saved { get; set; }
        }
    }
}