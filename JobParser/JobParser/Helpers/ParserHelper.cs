using System.Net;
using System.Text.RegularExpressions;

namespace JobParser.Helpers;

public static class ParserHelper
{
    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public static List<string> ExtractRawPhones(string text)
    {
        var rawPhones = Regex
            .Matches(text, @"\+?[\d][\d\s\-\.\(\)]{7,20}[\d]")
            .Select(m => Regex.Replace(m.Value, @"[^\d+]", ""))
            .Where(p => p.Count(char.IsDigit) is >= 7 and <= 15)
            .Distinct()
            .ToList();

        return rawPhones
            .Where(phone => !IsDateOrSalary(phone))
            .ToList();
    }

    private static bool IsDateOrSalary(string phone)
    {
        var digitsOnly = phone.Replace("+", "");

        if (digitsOnly.Length < 7 || digitsOnly.Length > 15)
            return true;

        if (Regex.IsMatch(digitsOnly, @"^(0[1-9]|[12][0-9]|3[01])(0[1-9]|1[0-2])20\d{2}$"))
            return true;

        if (Regex.IsMatch(digitsOnly, @"^(0[1-9]|[12][0-9]|3[01])(0[1-9]|1[0-2])\d{2}$") && digitsOnly.Length == 6)
            return true;

        if (Regex.IsMatch(digitsOnly, @"^[1-9]\d{3}0{4}$"))
            return true;

        if (digitsOnly.Length == 8 &&
            digitsOnly.Substring(4, 4).All(c => c == '0' || c == '5') &&
            digitsOnly.EndsWith("00"))
            return true;

        if (digitsOnly.Distinct().Count() == 1)
            return true;

        if (IsSequentialDigits(digitsOnly))
            return true;

        if (Regex.IsMatch(digitsOnly, @"^[1-9]0+$"))
            return true;

        return false;
    }

    private static bool IsSequentialDigits(string number)
    {
        if (number.Length < 4) return false;

        bool ascending = true;
        bool descending = true;

        for (int i = 1; i < number.Length; i++)
        {
            if (number[i] != number[i - 1] + 1)
                ascending = false;
            if (number[i] != number[i - 1] - 1)
                descending = false;
        }

        return ascending || descending;
    }

    public static string? ExtractEmail(string html)
    {
        var mailtoMatch = Regex.Match(html,
            @"mailto:([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})",
            RegexOptions.IgnoreCase);

        if (mailtoMatch.Success)
            return mailtoMatch.Groups[1].Value.ToLower();

        var emailMatch = Regex.Match(html,
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
            RegexOptions.IgnoreCase);

        return emailMatch.Success ? emailMatch.Value.ToLower() : null;
    }

    public static List<string> ExtractJobUrls(string html, string domain, string pattern)
    {
        return Regex
            .Matches(html, pattern)
            .Select(m => domain + m.Groups[1].Value)
            .Where(url => !url.Contains('?'))
            .Distinct()
            .ToList();
    }

    public static string? ExtractLocation(string html, string fullText)
    {
        var usaCityMatch = Regex.Match(html, @"США\s*\(([^)]{2,50})\)", RegexOptions.IgnoreCase);
        if (usaCityMatch.Success)
            return usaCityMatch.Groups[1].Value.Trim();

        var cities = new[]
        {
            "Нью-Йорк", "Лос-Анджелес", "Чикаго", "Майами", "Акрон",
            "Лас-Вегас", "Денвер", "Бостон", "Сан-Франциско", "Хьюстон",
            "New York", "Los Angeles", "Chicago", "Miami", "Houston",
            "Балтимор", "Baltimore"
        };

        return cities.FirstOrDefault(city =>
            fullText.Contains(city, StringComparison.OrdinalIgnoreCase));
    }
}