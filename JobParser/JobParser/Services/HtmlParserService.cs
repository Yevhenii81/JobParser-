using HtmlAgilityPack;
using JobParser.Helpers;
using JobParser.Models;

namespace JobParser.Services;

public class HtmlParserService
{
    private readonly ILogger<HtmlParserService> _logger;

    public HtmlParserService(ILogger<HtmlParserService> logger)
    {
        _logger = logger;
    }

    public JobLead? ParseJobPage(string html, string url, string source)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractTitle(doc);
        var description = ExtractDescription(doc);
        var phones = ParserHelper.ExtractRawPhones(doc.DocumentNode.InnerText);
        var email = ParserHelper.ExtractEmail(html);
        var location = ExtractLocation(doc, html);

        if (phones.Count == 0 && email == null)
        {
            _logger.LogDebug("Нет контактов: {Url}", url);
            return null;
        }

        return new JobLead
        {
            Title = title,
            Description = description,
            PhoneNumbers = phones,
            Email = email,
            Location = location,
            Source = source,
            ParsedAt = DateTime.UtcNow
        };
    }

    private static string ExtractTitle(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//h1");
        var title = titleNode?.InnerText ?? "Без заголовка";
        return ParserHelper.CleanText(title);
    }

    private static string ExtractDescription(HtmlDocument doc)
    {
        var descNode = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'vacancy-description')] | " +
            "//div[contains(@class,'description')] | " +
            "//div[contains(@class,'vacancy-body')] | " +
            "//div[contains(@class,'vacancy__description')]");

        if (descNode == null) return string.Empty;

        var description = ParserHelper.CleanText(descNode.InnerText);
        return description.Length > 1000 ? description[..1000] + "..." : description;
    }

    private static string? ExtractLocation(HtmlDocument doc, string html)
    {
        var locNode = doc.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'vacancy-location')] | " +
            "//*[contains(@class,'location')] | " +
            "//*[contains(@class,'vacancy__location')]");

        if (locNode != null)
        {
            var location = ParserHelper.CleanText(locNode.InnerText);
            var cityMatch = System.Text.RegularExpressions.Regex.Match(location, @"США\s*\(([^)]+)\)");
            return cityMatch.Success ? cityMatch.Groups[1].Value.Trim() : location;
        }

        return ParserHelper.ExtractLocation(html, doc.DocumentNode.InnerText);
    }
}