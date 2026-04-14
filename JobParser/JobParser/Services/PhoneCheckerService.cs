using System.Text;
using System.Text.Json;

namespace JobParser.Services
{
    public class PhoneCheckerService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<PhoneCheckerService> _logger;
        private readonly string _apiUrl;

        public PhoneCheckerService(
            HttpClient httpClient,
            ILogger<PhoneCheckerService> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;

            _apiUrl = configuration["ParserSettings:PhoneCheckApiUrl"]
                ?? throw new InvalidOperationException("PhoneCheckApiUrl не настроен в appsettings.json");

            _logger.LogInformation("PhoneCheckerService mode: External API -> {ApiUrl}", _apiUrl);
        }

        public async Task<bool> PhoneExistsAsync(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                _logger.LogWarning("Пустой номер пропущен");
                return true;
            }

            try
            {
                var requestBody = new { PhoneNumber = phoneNumber };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_apiUrl, content);

                if (response.StatusCode == System.Net.HttpStatusCode.Created)
                {
                    _logger.LogDebug("Номер новый: {Phone}", phoneNumber);
                    return false;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    _logger.LogDebug("Номер существует: {Phone}", phoneNumber);
                    return true;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning("Невалидный номер: {Phone}", phoneNumber);
                    return true;
                }

                _logger.LogWarning("Неожиданный статус {Status} для {Phone}",
                    (int)response.StatusCode, phoneNumber);
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "PhoneNumberAPI недоступен. Проверьте что он запущен!");
                throw new InvalidOperationException(
                    "PhoneNumberAPI недоступен. Запустите PhoneNumberAPI перед запуском JobParser", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Timeout при обращении к PhoneNumberAPI");
                throw new InvalidOperationException(
                    "PhoneNumberAPI не отвечает (timeout)", ex);
            }
        }

        public async Task<bool> AnyPhoneExistsAsync(List<string> phoneNumbers)
        {
            if (phoneNumbers == null || phoneNumbers.Count == 0)
                return false;

            foreach (var phone in phoneNumbers)
            {
                if (await PhoneExistsAsync(phone))
                    return true;
            }

            return false;
        }
    }
}