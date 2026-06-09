using System.ComponentModel.DataAnnotations;

namespace JobAutomation.Models;

public enum ScoutedJobStatus
{
    New,
    SentToTelegram,
    IgnoredByGemini,
    Applied
}

public class ScoutedJob
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(500)]
    public string LinkedInUrl { get; set; } = string.Empty;

    [MaxLength(5000)]
    public string? RawText { get; set; }

    [MaxLength(200)]
    public string? KeywordMatched { get; set; }

    public ScoutedJobStatus Status { get; set; } = ScoutedJobStatus.New;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Which board this job was scouted from
    [MaxLength(50)]
    public string Board { get; set; } = "LinkedIn";
}
