using Newtonsoft.Json;
using JobAutomation.DTOs;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class ResumeMatchingService
{
    private readonly ILogger<ResumeMatchingService> _logger;

    public ResumeMatchingService(ILogger<ResumeMatchingService> logger)
    {
        _logger = logger;
    }

    public ResumeMatchResult Match(JobExtractionResult job, Resume resume)
    {
        try
        {
            var resumeSkills = ParseSkills(resume.Skills);
            var jobSkills = ParseJobSkills(job.RequiredSkills);

            var matching = new List<string>();
            var missing = new List<string>();

            foreach (var skill in jobSkills)
            {
                if (resumeSkills.Any(rs => IsSkillMatch(rs, skill)))
                {
                    matching.Add(skill);
                }
                else
                {
                    missing.Add(skill);
                }
            }

            int matchPercentage = jobSkills.Count > 0
                ? (int)Math.Round((double)matching.Count / jobSkills.Count * 100)
                : 0;

            // ATS score considers skills match + experience + resume completeness
            int atsScore = CalculateAtsScore(matchPercentage, resume, job);

            return new ResumeMatchResult
            {
                MatchingSkills = matching,
                MissingSkills = missing,
                MatchPercentage = matchPercentage,
                AtsScore = atsScore,
                Summary = GenerateSummary(matching, missing, matchPercentage)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during resume matching");
            return new ResumeMatchResult
            {
                MatchPercentage = 0,
                AtsScore = 0,
                Summary = "Unable to perform skill matching."
            };
        }
    }

    private List<string> ParseSkills(string? skillsJson)
    {
        if (string.IsNullOrWhiteSpace(skillsJson))
            return new List<string>();

        try
        {
            var skills = JsonConvert.DeserializeObject<List<string>>(skillsJson);
            return skills?.Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList()
                   ?? new List<string>();
        }
        catch
        {
            // Fallback: try comma-separated
            return skillsJson.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
        }
    }

    private List<string> ParseJobSkills(string? requiredSkills)
    {
        if (string.IsNullOrWhiteSpace(requiredSkills))
            return new List<string>();

        return requiredSkills.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
    }

    private bool IsSkillMatch(string resumeSkill, string jobSkill)
    {
        var rs = resumeSkill.ToLowerInvariant().Trim();
        var js = jobSkill.ToLowerInvariant().Trim();

        // Exact match
        if (rs == js) return true;

        // Contains match (e.g., "ASP.NET Core" contains "ASP.NET")
        if (rs.Contains(js) || js.Contains(rs)) return true;

        // Common abbreviation mappings
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "javascript", new[] { "js", "es6", "es2015" } },
            { "typescript", new[] { "ts" } },
            { "c#", new[] { "csharp", "c sharp", ".net" } },
            { "asp.net", new[] { "aspnet", "asp.net core", "aspnetcore" } },
            { "sql server", new[] { "mssql", "ms sql", "t-sql", "tsql" } },
            { "postgresql", new[] { "postgres", "psql" } },
            { "react", new[] { "reactjs", "react.js" } },
            { "angular", new[] { "angularjs", "angular.js" } },
            { "node", new[] { "nodejs", "node.js" } },
            { "python", new[] { "py" } },
            { "machine learning", new[] { "ml" } },
            { "artificial intelligence", new[] { "ai" } },
            { "amazon web services", new[] { "aws" } },
            { "google cloud platform", new[] { "gcp" } },
            { "continuous integration", new[] { "ci/cd", "cicd" } },
        };

        foreach (var (key, values) in aliases)
        {
            var allForms = values.Append(key).Select(v => v.ToLowerInvariant()).ToList();
            if (allForms.Contains(rs) && allForms.Contains(js))
                return true;
            if (allForms.Any(f => rs.Contains(f)) && allForms.Any(f => js.Contains(f)))
                return true;
        }

        return false;
    }

    private int CalculateAtsScore(int skillMatchPct, Resume resume, JobExtractionResult job)
    {
        double score = 0;

        // Skills match: 60% weight
        score += skillMatchPct * 0.60;

        // Resume completeness: 20% weight
        int completeness = 0;
        if (!string.IsNullOrWhiteSpace(resume.FullText)) completeness += 25;
        if (!string.IsNullOrWhiteSpace(resume.Skills)) completeness += 25;
        if (!string.IsNullOrWhiteSpace(resume.Experience)) completeness += 25;
        if (!string.IsNullOrWhiteSpace(resume.Education)) completeness += 25;
        score += completeness * 0.20;

        // Experience relevance: 20% weight (basic heuristic)
        if (!string.IsNullOrWhiteSpace(resume.Experience) && !string.IsNullOrWhiteSpace(job.ExperienceRequired))
        {
            score += 20; // Having any experience listed gets full credit for this component
        }
        else if (!string.IsNullOrWhiteSpace(resume.Experience))
        {
            score += 10;
        }

        return Math.Min(100, (int)Math.Round(score));
    }

    private string GenerateSummary(List<string> matching, List<string> missing, int matchPct)
    {
        var parts = new List<string>();

        if (matching.Count > 0)
            parts.Add($"Matching skills: {string.Join(", ", matching)}.");

        if (missing.Count > 0)
            parts.Add($"Missing skills: {string.Join(", ", missing)}.");

        parts.Add($"Overall match: {matchPct}%.");

        if (matchPct >= 70)
            parts.Add("Strong match! Recommended to apply.");
        else if (matchPct >= 40)
            parts.Add("Moderate match. Consider applying.");
        else
            parts.Add("Low match. Review the requirements carefully.");

        return string.Join(" ", parts);
    }
}
