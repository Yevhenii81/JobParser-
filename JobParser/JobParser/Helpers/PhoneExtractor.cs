using System.Text.RegularExpressions;

namespace JobParser.Helpers
{
    public static class PhoneExtractor
    {
        private static readonly Regex PhoneRegex = new(
            @"\+?[\d][\d\s\-\.\(\)]{7,20}[\d]",
            RegexOptions.Compiled
        );
        private const int MinDigits = 7;
        private const int MaxDigits = 15;
        public static List<string> ExtractPhones(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var phones = new HashSet<string>();
            var matches = PhoneRegex.Matches(text);

            foreach (Match match in matches)
            {
                var cleaned = CleanPhone(match.Value);

                var digitCount = cleaned.Count(char.IsDigit);
                if (digitCount < MinDigits || digitCount > MaxDigits)
                    continue;

                if (!phones.Contains(cleaned))
                    phones.Add(cleaned);
            }
            return phones.ToList();
        }
        private static string CleanPhone(string phone)
        {
            return Regex.Replace(phone, @"[^\d+]", "").Trim();
        }
    }
}