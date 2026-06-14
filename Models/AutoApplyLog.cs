using System.ComponentModel.DataAnnotations;

namespace JobAutomation.Models;

public enum AutoApplyStatus
{
    Applied,
    Skipped,
    Failed
}

public class AutoApplyLog
{
    [Key]
    public int Id { get; set; }

    [Required][MaxLength(200)]
    public string CompanyName { get; set; } = string.Empty;

    [Required][MaxLength(200)]
    public string JobTitle { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? JobId { get; set; }

    [MaxLength(500)]
    public string? JobUrl { get; set; }

    [MaxLength(100)]
    public string? WorkMode { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(100)]
    public string? ExperienceRequired { get; set; }

    [MaxLength(100)]
    public string? SalaryRange { get; set; }

    [MaxLength(50)]
    public string Platform { get; set; } = "LinkedIn";

    public AutoApplyStatus Status { get; set; }

    [MaxLength(500)]
    public string? SkipReason { get; set; }

    public bool EmailSent { get; set; }

    public bool TelegramNotified { get; set; }

    public int? JobPostId { get; set; }

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}
