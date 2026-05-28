using System.ComponentModel.DataAnnotations;

namespace JobAutomation.Models;

public class Resume
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string FullText { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of skill strings, e.g. ["C#", "ASP.NET Core", "SQL"]
    /// </summary>
    public string? Skills { get; set; }

    /// <summary>
    /// JSON array of experience objects
    /// </summary>
    public string? Experience { get; set; }

    [MaxLength(2000)]
    public string? Education { get; set; }

    [MaxLength(500)]
    public string? FilePath { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
