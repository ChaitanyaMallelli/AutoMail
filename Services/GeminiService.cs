using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JobAutomation.DTOs;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _apiKey = configuration["GeminiApi:ApiKey"] ?? throw new ArgumentNullException("GeminiApi:ApiKey is not configured");
        _model = configuration["GeminiApi:Model"] ?? "gemini-2.5-flash";
        _logger = logger;
    }

    public async Task<JobExtractionResult> ExtractJobDetailsFromTextAsync(string text)
    {
        var prompt = BuildExtractionPrompt(text);
        var response = await CallGeminiAsync(prompt);
        return ParseExtractionResponse(response, text);
    }

    public async Task<JobExtractionResult> ExtractJobDetailsFromImageAsync(byte[] imageBytes, string mimeType = "image/png")
    {
        var prompt = BuildImageExtractionPrompt();
        var response = await CallGeminiWithImageAsync(prompt, imageBytes, mimeType);
        return ParseExtractionResponse(response, "[Image upload]");
    }

    public async Task<JobExtractionResult> ExtractJobDetailsFromPdfAsync(byte[] pdfBytes)
    {
        var prompt = BuildImageExtractionPrompt();
        var response = await CallGeminiWithImageAsync(prompt, pdfBytes, "application/pdf");
        return ParseExtractionResponse(response, "[PDF upload]");
    }

    public async Task<EmailDraftDto> GenerateEmailAsync(JobExtractionResult job, Resume resume, UserProfile profile, ResumeMatchResult matchResult)
    {
        try
        {
            var prompt = BuildEmailPrompt(job, resume, profile, matchResult);
            var response = await CallGeminiAsync(prompt);
            return ParseEmailResponse(response, job.RecruiterEmail ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating email via Gemini");
            return new EmailDraftDto
            {
                IsSuccessful = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private string BuildExtractionPrompt(string text)
    {
        return $@"You are a job posting analyzer. Extract the following details from this job posting text.
Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{{
  ""companyName"": ""extracted company name or 'Unknown'"",
  ""role"": ""job title/role"",
  ""requiredSkills"": ""comma-separated list of required skills"",
  ""recruiterEmail"": ""email address if found, or null"",
  ""experienceRequired"": ""experience requirement e.g. '2-4 years' or null"",
  ""location"": ""job location or 'Remote' or null""
}}

Rules:
- Extract information accurately from the text.
- If a field is not found, use null.
- For recruiterEmail, look for email addresses in the text carefully.
- For skills, list all technical and soft skills mentioned.
- Be thorough but accurate. Never make up information.

Job posting text:
{text}";
    }

    private string BuildImageExtractionPrompt()
    {
        return @"You are a job posting analyzer. Look at this image which contains a job posting, LinkedIn hiring post, or job description.
Extract the following details from the image.
Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{
  ""companyName"": ""extracted company name or 'Unknown'"",
  ""role"": ""job title/role"",
  ""requiredSkills"": ""comma-separated list of required skills"",
  ""recruiterEmail"": ""email address if found, or null"",
  ""experienceRequired"": ""experience requirement e.g. '2-4 years' or null"",
  ""location"": ""job location or 'Remote' or null""
}

Rules:
- Read all text visible in the image carefully.
- Handle noisy screenshots, LinkedIn posts, and various formats.
- Extract recruiter/HR email addresses if visible.
- If a field is not clearly visible, use null.
- Never fabricate or guess information not in the image.";
    }

    private string BuildEmailPrompt(JobExtractionResult job, Resume resume, UserProfile profile, ResumeMatchResult matchResult)
    {
        var matchingSkillsText = matchResult.MatchingSkills.Any()
            ? string.Join(", ", matchResult.MatchingSkills)
            : "general professional skills";

        return $@"Generate a professional job application email. Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{{
  ""subject"": ""email subject line"",
  ""body"": ""full email body as plain text""
}}

Use these REAL details about the applicant:
- Name: {profile.FullName}
- Email: {profile.Email}
- Phone: {profile.Phone ?? "N/A"}
- LinkedIn: {profile.LinkedInUrl ?? "N/A"}

Job details:
- Company: {job.CompanyName}
- Role: {job.Role}
- Required Skills: {job.RequiredSkills}
- Experience Required: {job.ExperienceRequired ?? "Not specified"}
- Location: {job.Location ?? "Not specified"}

Applicant's MATCHING skills (use ONLY these, never fabricate):
{matchingSkillsText}

Match score: {matchResult.MatchPercentage}%

Email format rules:
- Professional subject line mentioning the role
- Proper greeting (Dear Hiring Manager/HR Team)
- Brief introduction (1-2 sentences)
- Mention ONLY the matching skills listed above
- NEVER claim skills or experience the applicant doesn't have
- Professional closing with contact details
- Keep it concise (under 200 words)
- Plain text only, no HTML
- Warm but professional tone";
    }

    private async Task<string> CallGeminiAsync(string prompt)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.3,
                maxOutputTokens = 2048
            }
        };

        var json = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Gemini API with text prompt...");
        var response = await _httpClient.PostAsync(url, content);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error: {StatusCode} - {Response}", response.StatusCode, responseString);
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {responseString}");
        }

        return ExtractTextFromGeminiResponse(responseString);
    }

    private async Task<string> CallGeminiWithImageAsync(string prompt, byte[] fileBytes, string mimeType)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var base64Data = Convert.ToBase64String(fileBytes);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = base64Data
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.3,
                maxOutputTokens = 2048
            }
        };

        var json = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Gemini API with image/file ({MimeType})...", mimeType);
        var response = await _httpClient.PostAsync(url, content);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error: {StatusCode} - {Response}", response.StatusCode, responseString);
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {responseString}");
        }

        return ExtractTextFromGeminiResponse(responseString);
    }

    private string ExtractTextFromGeminiResponse(string responseJson)
    {
        try
        {
            var jObject = JObject.Parse(responseJson);
            var text = jObject["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
            return text ?? throw new Exception("No text content in Gemini response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini response: {Response}", responseJson);
            throw;
        }
    }

    private JobExtractionResult ParseExtractionResponse(string geminiResponse, string rawContent)
    {
        try
        {
            // Clean up response - remove markdown code fences if present
            var cleaned = geminiResponse.Trim();
            if (cleaned.StartsWith("```json"))
                cleaned = cleaned[7..];
            else if (cleaned.StartsWith("```"))
                cleaned = cleaned[3..];
            if (cleaned.EndsWith("```"))
                cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();

            var result = JsonConvert.DeserializeObject<Dictionary<string, string?>>(cleaned);
            if (result == null)
                throw new Exception("Failed to deserialize extraction result");

            return new JobExtractionResult
            {
                CompanyName = result.GetValueOrDefault("companyName") ?? "Unknown",
                Role = result.GetValueOrDefault("role") ?? "Unknown Role",
                RequiredSkills = result.GetValueOrDefault("requiredSkills") ?? "",
                RecruiterEmail = result.GetValueOrDefault("recruiterEmail"),
                ExperienceRequired = result.GetValueOrDefault("experienceRequired"),
                Location = result.GetValueOrDefault("location"),
                RawContent = rawContent,
                IsSuccessful = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse job extraction result from Gemini response");
            return new JobExtractionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Failed to parse AI response: {ex.Message}",
                RawContent = rawContent
            };
        }
    }

    private EmailDraftDto ParseEmailResponse(string geminiResponse, string recipientEmail)
    {
        try
        {
            var cleaned = geminiResponse.Trim();
            if (cleaned.StartsWith("```json"))
                cleaned = cleaned[7..];
            else if (cleaned.StartsWith("```"))
                cleaned = cleaned[3..];
            if (cleaned.EndsWith("```"))
                cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();

            var result = JsonConvert.DeserializeObject<Dictionary<string, string?>>(cleaned);
            if (result == null)
                throw new Exception("Failed to deserialize email result");

            return new EmailDraftDto
            {
                Subject = result.GetValueOrDefault("subject") ?? "Job Application",
                Body = result.GetValueOrDefault("body") ?? "",
                RecipientEmail = recipientEmail,
                IsSuccessful = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse email generation result from Gemini response");
            return new EmailDraftDto
            {
                IsSuccessful = false,
                ErrorMessage = $"Failed to parse AI response: {ex.Message}"
            };
        }
    }
}
