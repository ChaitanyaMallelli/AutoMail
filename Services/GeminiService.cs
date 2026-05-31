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

    public async Task<JobExtractionResult> ExtractJobDetailsFromUrlContentAsync(string htmlContent, string url)
    {
        var prompt = $@"You are a job posting analyzer. The following is the HTML/text content fetched from a job listing URL ({url}).
Extract the following details from this content.
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
- Extract information accurately from the content.
- Ignore navigation menus, footers, ads, and other non-job content.
- If a field is not found, use null.
- For recruiterEmail, look for email addresses carefully.
- For skills, list all technical and soft skills mentioned.
- Be thorough but accurate. Never make up information.

Content:
{htmlContent[..Math.Min(htmlContent.Length, 8000)]}";

        var response = await CallGeminiAsync(prompt);
        return ParseExtractionResponse(response, url);
    }

    public async Task<ResumeExtractionResult> ExtractResumeDetailsFromPdfAsync(byte[] pdfBytes)
    {
        try
        {
            var prompt = @"You are a resume/CV parser. Analyze this PDF resume and extract structured information.
Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{
  ""fullText"": ""complete text content of the resume"",
  ""skills"": [""skill1"", ""skill2"", ""skill3""],
  ""experience"": ""brief summary of work experience including companies, roles, and durations"",
  ""education"": ""education details including degrees, institutions, and years""
}

Rules:
- Extract ALL technical skills, tools, frameworks, and languages mentioned
- Include soft skills if explicitly mentioned
- For experience, summarize each role briefly
- For education, include degree, institution, and graduation year
- Be thorough and accurate. Extract everything visible in the resume.";

            var response = await CallGeminiWithImageAsync(prompt, pdfBytes, "application/pdf");

            var cleaned = CleanJsonResponse(response);
            var result = JsonConvert.DeserializeObject<Dictionary<string, object?>>(cleaned);
            if (result == null)
                throw new Exception("Failed to deserialize resume extraction result");

            var skills = new List<string>();
            if (result.TryGetValue("skills", out var skillsObj) && skillsObj != null)
            {
                var skillsArray = JArray.Parse(skillsObj.ToString()!);
                skills = skillsArray.Select(s => s.ToString()).ToList();
            }

            return new ResumeExtractionResult
            {
                FullText = result.GetValueOrDefault("fullText")?.ToString() ?? "",
                Skills = skills,
                Experience = result.GetValueOrDefault("experience")?.ToString(),
                Education = result.GetValueOrDefault("education")?.ToString(),
                IsSuccessful = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract resume details from PDF via Gemini");
            return new ResumeExtractionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Failed to parse resume: {ex.Message}"
            };
        }
    }

    public async Task<EmailDraftDto> GenerateEmailAsync(JobExtractionResult job, Resume resume, UserProfile profile, ResumeMatchResult matchResult, string tone = "professional")
    {
        try
        {
            var prompt = BuildEmailPrompt(job, resume, profile, matchResult, tone);
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

    public async Task<EmailDraftDto> GenerateFollowUpEmailAsync(JobPost job, UserProfile profile)
    {
        try
        {
            var prompt = $@"Generate a polite professional follow-up email for a job application. Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{{
  ""subject"": ""email subject line"",
  ""body"": ""full email body as plain text""
}}

Context:
- Applicant Name: {profile.FullName}
- Applicant Email: {profile.Email}
- Applicant Phone: {profile.Phone ?? "N/A"}
- Company: {job.CompanyName}
- Role Applied For: {job.Role}
- Original Application Date: {job.CreatedAt:MMMM dd, yyyy}
- Days Since Application: {(DateTime.UtcNow - job.CreatedAt).Days}

Email rules:
- Polite and professional follow-up tone
- Reference the original application date
- Express continued interest in the role
- Keep it brief (under 120 words)
- Don't be pushy or demanding
- Include contact details in the closing
- Plain text only, no HTML";

            var response = await CallGeminiAsync(prompt);
            return ParseEmailResponse(response, job.RecruiterEmail ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating follow-up email via Gemini");
            return new EmailDraftDto
            {
                IsSuccessful = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> ProcessMockInterviewAudioAsync(byte[] audioBytes, JobPost job, UserProfile profile, string conversationHistory)
    {
        try
        {
            var prompt = $@"You are a professional corporate recruiter conducting a voice mock interview.
Your goal is to interview the applicant for the role of '{job.Role}' at '{job.CompanyName}'.
The applicant's name is {profile.FullName}.

Here is the job description you are hiring for:
{job.RawContent ?? job.RequiredSkills}

Here is the conversation history so far:
{conversationHistory}

Listen to the applicant's attached voice message.
Respond directly to their voice message as the recruiter. 
Keep your response conversational, concise (under 3 sentences), and natural to be spoken aloud.
Do not use markdown, bullet points, or complex formatting. Just plain conversational text.
If they just joined, greet them and ask the first interview question.";

            var response = await CallGeminiWithImageAsync(prompt, audioBytes, "audio/ogg");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing mock interview audio via Gemini");
            return "I'm sorry, I encountered an error processing your voice. Could you please try again?";
        }
    }

    public async Task<string> ProcessLiveInterviewAudioAsync(byte[] audioBytes, JobPost job)
    {
        try
        {
            var prompt = $@"You are a live interview Co-Pilot. You are listening to a live job interview for the role of '{job.Role}' at '{job.CompanyName}'.
Job context: {job.RawContent ?? job.RequiredSkills}

Listen to the attached audio chunk from the live interview.
If you hear the recruiter asking a question, instantly output 2-3 extremely concise bullet points with suggested talking points for the applicant.
If there is no question, or just casual chatter, return the word 'SILENCE'.
Do not output greetings or conversation. ONLY bullet points. Keep it extremely brief so the applicant can read it instantly.";

            var response = await CallGeminiWithImageAsync(prompt, audioBytes, "audio/webm");
            
            // Filter out empty or non-helpful responses
            if (response.Contains("SILENCE", StringComparison.OrdinalIgnoreCase) || response.Length < 10)
                return "";

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing live interview audio chunk");
            return "";
        }
    }

    public async Task<bool> IsPostRelevantAsync(string postText)
    {
        try
        {
            var prompt = $@"You are a strict HR filter. Read the following LinkedIn post.
Determine if the post is explicitly hiring a software developer AND explicitly requires exactly or more than 3 years of experience.

If it explicitly mentions '3+ years', '3-5 years', 'minimum 3 years', etc., return exactly the word TRUE.
If experience is not mentioned, or it is less than 3 years (e.g. '0-2 years', 'fresher'), return exactly the word FALSE.
If the post is not a job opening, return exactly the word FALSE.

Post text:
{postText}";

            var response = await CallGeminiAsync(prompt);
            return response.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filtering post via Gemini");
            return false; // Safely ignore if API fails
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

    private string BuildEmailPrompt(JobExtractionResult job, Resume resume, UserProfile profile, ResumeMatchResult matchResult, string tone = "professional")
    {
        var matchingSkillsText = matchResult.MatchingSkills.Any()
            ? string.Join(", ", matchResult.MatchingSkills)
            : "general professional skills";

        var toneInstruction = tone.ToLower() switch
        {
            "enthusiastic" => @"
Tone rules:
- Show genuine enthusiasm and passion for the role
- Use energetic but professional language
- Express excitement about the company and opportunity
- Be warm and personable while remaining professional
- Use active, dynamic verbs",
            "concise" => @"
Tone rules:
- Be extremely brief and to-the-point
- Maximum 80 words for the body
- No filler phrases or unnecessary pleasantries
- State qualifications directly
- Get straight to the value proposition",
            _ => @"
Tone rules:
- Professional and formal tone
- Warm but business-appropriate language
- Structured and clear communication
- Standard corporate email format"
        };

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
{toneInstruction}

Email format rules:
- Professional subject line mentioning the role
- Proper greeting (Dear Hiring Manager/HR Team)
- Brief introduction (1-2 sentences)
- Mention ONLY the matching skills listed above
- NEVER claim skills or experience the applicant doesn't have
- Professional closing with contact details
- Keep it concise (under 200 words)
- Plain text only, no HTML";
    }

    public async Task<string> ClassifyReplyAsync(string replyText, string company, string role)
    {
        var prompt = $"""
            You are an AI assistant classifying recruiter email replies.

            A job seeker applied to {role} at {company}. The recruiter replied:

            ---
            {replyText.Substring(0, Math.Min(1500, replyText.Length))}
            ---

            Classify this reply into EXACTLY ONE of these categories (return only the category word, nothing else):
            - interview (recruiter wants to schedule an interview or call)
            - rejected (application was declined)
            - interested (recruiter is interested but no interview yet — asking for more info, portfolio, etc.)
            - other (anything else — OOO, acknowledgement, etc.)

            Return only the single category word.
            """;

        try
        {
            var result = await CallGeminiAsync(prompt);
            var cleaned = result.Trim().ToLower().Split('\n')[0].Trim();
            return cleaned is "interview" or "rejected" or "interested" or "other" ? cleaned : "other";
        }
        catch
        {
            return "other";
        }
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

    private string CleanJsonResponse(string geminiResponse)
    {
        var cleaned = geminiResponse.Trim();
        if (cleaned.StartsWith("```json"))
            cleaned = cleaned[7..];
        else if (cleaned.StartsWith("```"))
            cleaned = cleaned[3..];
        if (cleaned.EndsWith("```"))
            cleaned = cleaned[..^3];
        return cleaned.Trim();
    }

    private JobExtractionResult ParseExtractionResponse(string geminiResponse, string rawContent)
    {
        try
        {
            var cleaned = CleanJsonResponse(geminiResponse);

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
            var cleaned = CleanJsonResponse(geminiResponse);

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
