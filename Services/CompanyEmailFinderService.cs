using System.Text.RegularExpressions;

namespace JobAutomation.Services;

public class CompanyEmailFinderService
{
    private readonly GeminiService _geminiService;
    private readonly ILogger<CompanyEmailFinderService> _logger;

    private static readonly Regex EmailRegex = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Domains that are useless (generic form emails, noreply, etc.)
    private static readonly HashSet<string> BlockedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "noreply", "no-reply", "donotreply", "do-not-reply",
        "support", "info", "contact", "hello", "admin", "webmaster"
    };

    public CompanyEmailFinderService(GeminiService geminiService, ILogger<CompanyEmailFinderService> logger)
    {
        _geminiService = geminiService;
        _logger = logger;
    }

    /// <summary>
    /// Tries to find a recruiter or HR email for the given company.
    /// Returns null if nothing useful found.
    /// </summary>
    public async Task<string?> FindRecruiterEmailAsync(string companyName, string role)
    {
        _logger.LogInformation("Searching for recruiter email: {Company} — {Role}", companyName, role);

        try
        {
            // Strategy 1: Search company name + "careers" via HTTP
            var candidates = await ScrapeCompanyWebsiteAsync(companyName);

            if (candidates.Any())
            {
                var best = PickBestEmail(candidates, companyName);
                if (best != null)
                {
                    _logger.LogInformation("Found recruiter email via website scrape: {Email}", best);
                    return best;
                }
            }

            // Strategy 2: Ask Gemini to suggest likely HR email patterns
            var geminiEmail = await AskGeminiForEmailAsync(companyName, role);
            if (!string.IsNullOrEmpty(geminiEmail))
            {
                _logger.LogInformation("Gemini suggested recruiter email: {Email}", geminiEmail);
                return geminiEmail;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email finder failed for {Company}", companyName);
        }

        return null;
    }

    private async Task<List<string>> ScrapeCompanyWebsiteAsync(string companyName)
    {
        var emails = new List<string>();

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");

            // Try common career/contact page patterns
            var slug = companyName.ToLower()
                .Replace(" ", "")
                .Replace("pvt", "").Replace("ltd", "").Replace(".", "").Trim();

            var urlsToTry = new[]
            {
                $"https://{slug}.com/careers",
                $"https://{slug}.com/contact",
                $"https://{slug}.in/careers",
                $"https://www.{slug}.com/careers",
                $"https://www.{slug}.com/contact-us"
            };

            foreach (var url in urlsToTry)
            {
                try
                {
                    var html = await http.GetStringAsync(url);
                    var found = EmailRegex.Matches(html)
                        .Select(m => m.Value.ToLower())
                        .Where(e => !IsBlockedEmail(e))
                        .Distinct()
                        .ToList();

                    emails.AddRange(found);
                    if (found.Any()) break; // Stop after first successful page
                }
                catch { /* URL not reachable — try next */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Website scrape failed for {Company}", companyName);
        }

        return emails.Distinct().ToList();
    }

    private async Task<string?> AskGeminiForEmailAsync(string companyName, string role)
    {
        try
        {
            var prompt = $"""
                You are helping find a recruiter's email address for a job application.

                Company: {companyName}
                Role: {role}

                Based on common HR email patterns for Indian tech companies, suggest the most likely
                recruiter or HR email address for this company.

                Rules:
                - Only return a real-looking email address, nothing else
                - Use patterns like: hr@company.com, careers@company.com, recruit@company.com
                - If the company name suggests it's a large MNC (TCS, Infosys, Wipro, etc.), return "N/A"
                - If you cannot make a reasonable guess, return "N/A"
                - Return ONLY the email address or "N/A", no explanation
                """;

            var result = await _geminiService.CallGeminiPublicAsync(prompt);
            result = result?.Trim();

            if (string.IsNullOrEmpty(result) || result == "N/A") return null;
            if (!EmailRegex.IsMatch(result)) return null;
            if (IsBlockedEmail(result)) return null;

            return result;
        }
        catch { return null; }
    }

    private static string? PickBestEmail(List<string> emails, string companyName)
    {
        // Prefer HR/recruiter-sounding emails
        var priority = new[] { "hr@", "recruit", "career", "hiring", "talent", "jobs@" };

        foreach (var prefix in priority)
        {
            var match = emails.FirstOrDefault(e => e.Contains(prefix));
            if (match != null) return match;
        }

        // Fall back to first non-blocked email
        return emails.FirstOrDefault();
    }

    private static bool IsBlockedEmail(string email)
    {
        var localPart = email.Split('@')[0];
        return BlockedPrefixes.Contains(localPart) ||
               email.Contains("example.com") ||
               email.Contains("test.com");
    }
}
