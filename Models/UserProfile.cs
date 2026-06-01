using System.ComponentModel.DataAnnotations;

namespace JobAutomation.Models;

public class UserProfile
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(300)]
    [Url]
    public string? LinkedInUrl { get; set; }

    // Persisted so scout alerts still work after job data is cleared
    public long? TelegramChatId { get; set; }
}
