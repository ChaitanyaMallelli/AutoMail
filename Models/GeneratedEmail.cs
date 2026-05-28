using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace JobAutomation.Models;

public class GeneratedEmail
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int JobPostId { get; set; }

    [ForeignKey(nameof(JobPostId))]
    public JobPost? JobPost { get; set; }

    [Required]
    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    [EmailAddress]
    public string RecipientEmail { get; set; } = string.Empty;

    public bool IsApproved { get; set; } = false;

    public bool IsSent { get; set; } = false;

    public DateTime? SentAt { get; set; }

    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
