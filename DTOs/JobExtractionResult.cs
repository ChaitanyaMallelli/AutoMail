namespace JobAutomation.DTOs;

public class JobExtractionResult
{
    public string CompanyName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RequiredSkills { get; set; } = string.Empty;
    public string? RecruiterEmail { get; set; }
    public string? ExperienceRequired { get; set; }
    public string? Location { get; set; }
    public string? RawContent { get; set; }
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
}
