namespace JobAutomation.DTOs;

public class ResumeMatchResult
{
    public List<string> MatchingSkills { get; set; } = new();
    public List<string> MissingSkills { get; set; } = new();
    public int MatchPercentage { get; set; }
    public int AtsScore { get; set; }
    public string Summary { get; set; } = string.Empty;
}
