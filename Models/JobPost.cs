using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace JobAutomation.Models;

public enum JobSource
{
    Telegram,
    Upload
}

public enum SourceType
{
    Text,
    Image,
    Pdf,
    Url
}

public enum JobStatus
{
    Pending,
    EmailGenerated,
    Approved,
    Sent,
    Failed,
    Skipped,
    SavedForLater,
    FollowUpSent,
    InterviewScheduled,
    Rejected,
    Offered,
    Ghosted
}

public class JobPost
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string CompanyName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Role { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? RequiredSkills { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? RecruiterEmail { get; set; }

    [MaxLength(100)]
    public string? ExperienceRequired { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    public JobSource Source { get; set; }

    public SourceType SourceType { get; set; }

    public string? RawContent { get; set; }

    [MaxLength(500)]
    public string? ImagePath { get; set; }

    [Range(0, 100)]
    public int AtsScore { get; set; }

    [Range(0, 100)]
    public int SkillMatchPercentage { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Telegram chat ID for sending follow-up reminders
    /// </summary>
    public long? TelegramChatId { get; set; }

    /// <summary>
    /// Whether a follow-up reminder has been sent for this job
    /// </summary>
    public bool FollowUpReminderSent { get; set; } = false;

    /// <summary>
    /// Free-text notes about the application response
    /// </summary>
    [MaxLength(2000)]
    public string? ResponseNotes { get; set; }

    // Navigation property
    public GeneratedEmail? GeneratedEmail { get; set; }
}
