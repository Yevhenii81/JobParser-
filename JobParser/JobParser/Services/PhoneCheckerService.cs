using PhoneNumbers;
using Microsoft.EntityFrameworkCore;
using JobParser.Data;
using JobParser.Models;
using System.Text;
using System.Text.Json;

namespace JobParser.Services
{
    public class PhoneCheckerService
    {
        private readonly AppDbContext _context;
        private readonly HttpClient? _httpClient;
        private readonly ILogger<PhoneCheckerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly PhoneNumberUtil _phoneUtil;

        private readonly bool _useExternalApi;
        private readonly string? _apiUrl;

        public PhoneCheckerService(
            AppDbContext context,
            HttpClient httpClient,
            ILogger<PhoneCheckerService> logger,
            IConfiguration configuration)
        {
            _context = context;
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
            _phoneUtil = PhoneNumberUtil.GetInstance();

            _useExternalApi = _configuration.GetValue<bool>("ParserSettings:UseExternalPhoneApi", false);
            _apiUrl = _configuration["ParserSettings:PhoneCheckApiUrl"];

            if (_useExternalApi && string.IsNullOrEmpty(_apiUrl))
            {
                _logger.LogWarning(
                    "UseExternalPhoneApi is enabled but PhoneCheckApiUrl is not configured. Falling back to local database.");
                _useExternalApi = false;
            }

            var mode = _useExternalApi
                ? $"External API -> {_apiUrl}"
                : "Local Database";

            _logger.LogInformation("PhoneCheckerService mode: {Mode}", mode);
        }

        public string? ValidateAndNormalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            try
            {
                PhoneNumbers.PhoneNumber phoneNumber;

                if (input.TrimStart().StartsWith("+"))
                    phoneNumber = _phoneUtil.Parse(input, null);
                else
                    phoneNumber = _phoneUtil.Parse(input, "US");

                if (!_phoneUtil.IsValidNumber(phoneNumber))
                {
                    _logger.LogDebug("Invalid phone: {Phone}", input);
                    return null;
                }

                return _phoneUtil.Format(phoneNumber, PhoneNumberFormat.E164);
            }
            catch (NumberParseException ex)
            {
                _logger.LogDebug(ex, "Phone parse error: {Phone}", input);
                return null;
            }
        }

        public async Task<bool> PhoneExistsAsync(string phoneNumber)
        {
            var normalizedPhone = ValidateAndNormalize(phoneNumber);

            if (normalizedPhone == null)
            {
                _logger.LogWarning("Invalid phone skipped: {Phone}", phoneNumber);
                return true;
            }

            return _useExternalApi
                ? await CheckViaExternalApiAsync(normalizedPhone)
                : await CheckViaLocalDatabaseAsync(normalizedPhone);
        }

        private async Task<bool> CheckViaExternalApiAsync(string normalizedPhone)
        {
            try
            {
                var requestBody = new { PhoneNumber = normalizedPhone };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient!.PostAsync(_apiUrl, content);

                if (response.StatusCode == System.Net.HttpStatusCode.Created)
                    return false;

                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    return true;

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    return true;

                return false;
            }
            catch (HttpRequestException)
            {
                return await CheckViaLocalDatabaseAsync(normalizedPhone);
            }
            catch (TaskCanceledException)
            {
                return await CheckViaLocalDatabaseAsync(normalizedPhone);
            }
            catch (Exception)
            {
                return await CheckViaLocalDatabaseAsync(normalizedPhone);
            }
        }

        private async Task<bool> CheckViaLocalDatabaseAsync(string normalizedPhone)
        {
            try
            {
                var exists = await _context.PhoneNumbers
                    .AnyAsync(p => p.Number == normalizedPhone);

                if (exists)
                    return true;

                _context.PhoneNumbers.Add(new PhoneRecord
                {
                    Number = normalizedPhone,
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();
                return false;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                return true;
            }
            catch
            {
                return false;
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

        public string? GetRegion(string phoneNumber)
        {
            try
            {
                PhoneNumbers.PhoneNumber parsed;

                if (phoneNumber.TrimStart().StartsWith("+"))
                    parsed = _phoneUtil.Parse(phoneNumber, null);
                else
                    parsed = _phoneUtil.Parse(phoneNumber, "US");

                return _phoneUtil.GetRegionCodeForNumber(parsed);
            }
            catch
            {
                return null;
            }
        }

        private bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            return ex.InnerException?.Message.Contains("duplicate key") == true ||
                   ex.InnerException?.Message.Contains("unique constraint") == true;
        }
    }
}