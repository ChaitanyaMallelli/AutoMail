using System.ComponentModel.DataAnnotations;

namespace JobAutomation.Models;

public class UserJobPreferences
{
    [Key]
    public int Id { get; set; }

    public bool AutoApplyEnabled { get; set; } = true;

    // Experience range filter — only auto-apply to jobs within this range
    public int MinExperienceYears { get; set; } = 3;
    public int MaxExperienceYears { get; set; } = 4;

    // Comma-separated: "Remote,Hybrid,Onsite" (null = accept all)
    [MaxLength(200)]
    public string? PreferredWorkModes { get; set; }

    // Comma-separated: "Bangalore,Dubai,Remote" (null = accept all)
    [MaxLength(500)]
    public string? PreferredLocations { get; set; }

    // Required skills the job must mention (comma-separated, null = no requirement)
    [MaxLength(1000)]
    public string? RequiredSkills { get; set; }

    // Salary filter in LPA (null = no filter)
    public int? MinSalaryLpa { get; set; }
    public int? MaxSalaryLpa { get; set; }

    // Companies to never apply to (comma-separated)
    [MaxLength(1000)]
    public string? ExcludedCompanies { get; set; }

    // Minimum ATS score to proceed with application
    public int MinAtsScore { get; set; } = 30;

    // Email for self-notifications and daily report (null = use UserProfile.Email)
    [MaxLength(200)]
    public string? NotificationEmail { get; set; }

    // UTC hour for daily report (15 = 9 PM IST, i.e. 15:00 UTC ≈ 20:30 IST)
    public int DailyReportUtcHour { get; set; } = 15;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
