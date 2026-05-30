namespace JobAutomation.DTOs;

public class ResumeExtractionResult
{
    public List<string> Skills { get; set; } = new();
    public string? Experience { get; set; }
    public string? Education { get; set; }
    public string FullText { get; set; } = string.Empty;
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
}
