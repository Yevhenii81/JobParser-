using CsvHelper;
using CsvHelper.Configuration;
using JobParser.Models;
using System.Globalization;

namespace JobParser.Services
{
    public class CsvExportService
    {
        private readonly string _outputFolder;
        private readonly ILogger<CsvExportService> _logger;

        public CsvExportService(IConfiguration configuration, ILogger<CsvExportService> logger)
        {
            _logger = logger;
            _outputFolder = configuration["ParserSettings:OutputFolder"] ?? "out";

            if (!Directory.Exists(_outputFolder))
            {
                Directory.CreateDirectory(_outputFolder);
                _logger.LogInformation("Создана папка: {Folder}", _outputFolder);
            }
        }

        public async Task<string?> ExportToCsvAsync(List<JobLead> leads)
        {
            if (leads == null || leads.Count == 0)
            {
                _logger.LogWarning("Нет данных для экспорта");
                return null;
            }

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy.MM.dd_HH.mm");
                var fileName = $"[{timestamp}]_leads.csv";
                var filePath = Path.Combine(_outputFolder, fileName);

                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    Delimiter = ","
                };

                await using var writer = new StreamWriter(filePath);
                await using var csv = new CsvWriter(writer, config);

                csv.WriteField("Title");
                csv.WriteField("Description");
                csv.WriteField("PhoneNumbers");
                csv.WriteField("Email");
                csv.WriteField("Location");
                csv.WriteField("Source");
                csv.WriteField("Region");
                csv.WriteField("ParsedAt");
                await csv.NextRecordAsync();

                foreach (var lead in leads)
                {
                    csv.WriteField(lead.Title);
                    csv.WriteField(lead.Description);
                    csv.WriteField(string.Join("; ", lead.PhoneNumbers));
                    csv.WriteField(lead.Email);
                    csv.WriteField(lead.Location);
                    csv.WriteField(lead.Source);
                    csv.WriteField(lead.Region);
                    csv.WriteField(lead.ParsedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                    await csv.NextRecordAsync();
                }

                _logger.LogInformation(
                    "Экспортировано {Count} записей в файл: {File}",
                    leads.Count,
                    fileName);

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка экспорта в CSV");
                throw;
            }
        }

        public int GetRecordCount(string filePath)
        {
            if (!File.Exists(filePath))
                return 0;

            var lines = File.ReadAllLines(filePath);

            if (lines.Length == 0)
                return 0;

            return lines.Skip(1).Count(line => !string.IsNullOrWhiteSpace(line));
        }
    }
}