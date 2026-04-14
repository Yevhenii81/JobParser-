using CsvHelper;
using CsvHelper.Configuration;
using JobParser.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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
                _logger.LogWarning("Нет лидов для экспорта");
                return null;
            }
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy.MM.dd_HH.mm");
                var fileName = $"{timestamp}_leads.csv";
                var filePath = Path.Combine(_outputFolder, fileName);
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    Delimiter = ",",
                    ShouldQuote = _ => true,
                    TrimOptions = TrimOptions.Trim
                };

                await using var writer = new StreamWriter(
                    filePath,
                    append: false,
                    encoding: new UTF8Encoding(true));
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
                    var title = CleanText(lead.Title, 200);
                    csv.WriteField(title);
                    var description = FormatDescription(lead.Description, 800);
                    csv.WriteField(description);
                    var phones = string.Join(" | ", lead.PhoneNumbers);
                    csv.WriteField(phones);
                    csv.WriteField(lead.Email ?? string.Empty);
                    var location = CleanText(lead.Location, 100);
                    csv.WriteField(location);
                    csv.WriteField(lead.Source ?? string.Empty);
                    csv.WriteField(lead.Region ?? string.Empty);
                    csv.WriteField(lead.ParsedAt.ToString("yyyy-MM-dd HH:mm:ss"));

                    await csv.NextRecordAsync();
                }

                _logger.LogInformation("Экспортировано {Count} лидов в файл: {File}",
                    leads.Count, fileName);

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка экспорта в CSV");
                throw;
            }
        }
        private static string CleanText(string? text, int maxLength = 0)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var cleaned = text
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");

            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            cleaned = cleaned.Replace("\"", "'");

            if (maxLength > 0 && cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength) + "...";
            }
            return cleaned;
        }
        private static string FormatDescription(string? description, int maxLength = 800)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;
            var cleaned = description
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");

            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            cleaned = Regex.Replace(cleaned, @"([.!?;:])([А-ЯA-ZА-ЩЬЮЯЇІЄҐ])", "$1 $2");
            cleaned = Regex.Replace(cleaned, @"([,])([А-ЯA-ZА-ЩЬЮЯЇІЄҐ])", "$1 $2");
            cleaned = Regex.Replace(cleaned, @"([.!?,;:])\1+", "$1");
            cleaned = Regex.Replace(cleaned, @"(\d)([А-ЯA-ZА-ЩЬЮЯЇІЄҐ])", "$1 $2");
            cleaned = cleaned.Replace("\"", "'");

            if (maxLength > 0 && cleaned.Length > maxLength)
            {
                var cutPosition = cleaned.LastIndexOf('.', maxLength);
                if (cutPosition < maxLength - 100)
                {
                    cutPosition = cleaned.LastIndexOf(' ', maxLength);
                }

                if (cutPosition > 0)
                {
                    cleaned = cleaned.Substring(0, cutPosition) + "...";
                }
                else
                {
                    cleaned = cleaned.Substring(0, maxLength) + "...";
                }
            }
            return cleaned.Trim();
        }
        public int GetRecordCount(string filePath)
        {
            if (!File.Exists(filePath))
                return 0;

            try
            {
                var lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                    return 0;

                return lines.Skip(1).Count(line => !string.IsNullOrWhiteSpace(line));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка подсчёта записей в файле {File}", filePath);
                return 0;
            }
        }
    }
}