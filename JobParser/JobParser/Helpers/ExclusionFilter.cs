namespace JobParser.Helpers
{
    public class ExclusionFilter
    {
        private readonly HashSet<string> _exclusions;
        private readonly ILogger<ExclusionFilter> _logger;
        public ExclusionFilter(string exclusionsFilePath, ILogger<ExclusionFilter> logger)
        {
            _logger = logger;
            _exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            LoadExclusions(exclusionsFilePath);
        }
        private void LoadExclusions(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath);
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                        {
                            _exclusions.Add(line.Trim());
                        }
                    }
                    _logger.LogInformation("Загружено {Count} слов-исключений", _exclusions.Count);
                }
                else
                {
                    _logger.LogWarning("Файл исключений не найден: {Path}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки слов-исключений");
            }
        }
        public bool ContainsExclusions(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return _exclusions.Any(exclusion =>
                text.Contains(exclusion, StringComparison.OrdinalIgnoreCase));
        }
    }
}