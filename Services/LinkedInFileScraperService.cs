using JobAutomation.Models;

namespace JobAutomation.Services;

/// <summary>
/// Reads curated LinkedIn post links from a text file (produced by the external scraper)
/// instead of scraping live. Format:
///   # comment lines (ignored)
///   ## [keyword]      -> section header; sets KeywordMatched for following links
///   https://www.linkedin.com/posts/...   -> a post link
/// Returns deduped <see cref="ScoutedJob"/>s ready for the auto-apply pipeline.
/// </summary>
public static class LinkedInFileScraperService
{
    public static string CanonicalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var normalized = url.Trim();
        normalized = normalized.Split('#')[0];
        normalized = normalized.TrimEnd('/');
        normalized = normalized.Split('?')[0];

        return normalized.Trim();
    }

    public static List<ScoutedJob> ParseLinks(string filePath)
    {
        var jobs = new List<ScoutedJob>();
        if (!File.Exists(filePath))
            return jobs;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentKeyword = "linkedin";

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Section header: "## [keyword]"
            if (line.StartsWith("## "))
            {
                var header = line[3..].Trim().Trim('[', ']').Trim();
                if (header.Length > 0) currentKeyword = header;
                continue;
            }

            // Other comments
            if (line.StartsWith("#")) continue;

            if (!line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            var canonical = CanonicalizeUrl(line);
            if (string.IsNullOrEmpty(canonical)) continue;
            if (!seen.Add(canonical)) continue;

            jobs.Add(new ScoutedJob
            {
                LinkedInUrl = canonical,
                KeywordMatched = currentKeyword,
                Board = "LinkedIn",
                JobId = AutoApplyService.ExtractJobId(canonical),
            });
        }

        return jobs;
    }
}
