using JobParser.Helpers;
using JobParser.Models;
using JobParser.Services.Interfaces;

namespace JobParser.Services
{
    public class ParserService
    {
        private readonly IEnumerable<ISiteParser> _parsers;
        private readonly PhoneCheckerService _phoneChecker;
        private readonly CsvExportService _csvExport;
        private readonly ExclusionFilter _exclusionFilter;
        private readonly ILogger<ParserService> _logger;

        public ParserService(
            IEnumerable<ISiteParser> parsers,
            PhoneCheckerService phoneChecker,
            CsvExportService csvExport,
            ExclusionFilter exclusionFilter,
            ILogger<ParserService> logger)
        {
            _parsers = parsers;
            _phoneChecker = phoneChecker;
            _csvExport = csvExport;
            _exclusionFilter = exclusionFilter;
            _logger = logger;
        }

        public async Task RunAsync()
        {
            _logger.LogInformation("Начало парсинга");
            var startTime = DateTime.Now;
            var allLeads = new List<JobLead>();
            var stats = new
            {
                Total = 0,
                InvalidPhones = 0,
                Excluded = 0,
                Duplicates = 0,
                Saved = 0
            };

            try
            {
                foreach (var parser in _parsers)
                {
                    _logger.LogInformation("Запуск парсера: {Parser}", parser.SiteName);
                    var leads = await parser.ParseAsync();
                    allLeads.AddRange(leads);
                }

                stats = stats with { Total = allLeads.Count };
                _logger.LogInformation("Всего получено объявлений: {Count}", allLeads.Count);

                var filteredLeads = allLeads.Where(lead =>
                {
                    var hasExclusion = _exclusionFilter.ContainsExclusions(lead.Title) ||
                                      _exclusionFilter.ContainsExclusions(lead.Description);
                    if (hasExclusion)
                    {
                        _logger.LogDebug("Исключено по фильтру: {Title}", lead.Title);
                    }
                    return !hasExclusion;
                }).ToList();

                stats = stats with { Excluded = allLeads.Count - filteredLeads.Count };

                var validLeads = new List<JobLead>();

                foreach (var lead in filteredLeads)
                {
                    if (lead.PhoneNumbers.Count == 0)
                    {
                        if (!string.IsNullOrEmpty(lead.Email))
                        {
                            validLeads.Add(lead);
                        }
                        continue;
                    }

                    var normalizedPhones = lead.PhoneNumbers
                        .Select(p => _phoneChecker.ValidateAndNormalize(p))
                        .Where(p => p != null)
                        .Cast<string>()
                        .Distinct()
                        .ToList();

                    if (normalizedPhones.Count == 0)
                    {
                        stats = stats with { InvalidPhones = stats.InvalidPhones + 1 };
                        _logger.LogDebug("Пропущено: невалидные номера: {Title}", lead.Title);
                        continue;
                    }

                    lead.PhoneNumbers = normalizedPhones;
                    lead.Region = _phoneChecker.GetRegion(normalizedPhones.First());

                    bool phoneExists = await _phoneChecker.AnyPhoneExistsAsync(normalizedPhones);

                    if (!phoneExists)
                    {
                        validLeads.Add(lead);
                    }
                    else
                    {
                        stats = stats with { Duplicates = stats.Duplicates + 1 };
                    }
                }

                stats = stats with { Saved = validLeads.Count };

                string? savedFile = null;
                if (validLeads.Count > 0)
                {
                    savedFile = await _csvExport.ExportToCsvAsync(validLeads);

                    if (savedFile != null)
                    {
                        var recordCount = _csvExport.GetRecordCount(savedFile);
                        _logger.LogInformation("Файл содержит {Count} записей", recordCount);
                    }
                }

                var duration = DateTime.Now - startTime;

                _logger.LogInformation(
                    "Статистика парсинга:\n" +
                    "Всего: {Total}\n" +
                    "Невалидные номера: {InvalidPhones}\n" +
                    "Исключено по фильтру: {Excluded}\n" +
                    "Дубликаты: {Duplicates}\n" +
                    "Сохранено новых: {Saved}\n" +
                    "Время выполнения: {Duration}",
                    stats.Total,
                    stats.InvalidPhones,
                    stats.Excluded,
                    stats.Duplicates,
                    stats.Saved,
                    duration.ToString(@"mm\:ss"));

                if (savedFile != null)
                {
                    _logger.LogInformation("Результаты сохранены в файл: {File}", savedFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка во время парсинга");
                throw;
            }

            _logger.LogInformation("Парсинг завершен");
        }
    }
}