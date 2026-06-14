using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeminiService> _logger;

    // ── Rate-limit resilience ────────────────────────────────────────────
    // Static so ALL scoped instances share the same limiter (one app-wide)
    private static readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;

    // 15 RPM limit → 4 seconds per request.  Use 4.5s for safety headroom.
    private const int MinGapMs = 4500;
    // Retry policy
    private const int MaxRetries = 5;
    private static readonly int[] RetryDelaysMs = { 5_000, 10_000, 20_000, 40_000, 60_000 };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public GeminiService(HttpClient httpClient, IConfiguration configuration, IMemoryCache cache, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _apiKey = configuration["GeminiApi:ApiKey"] ?? throw new ArgumentNullException("GeminiApi:ApiKey is not configured");
        _model = configuration["GeminiApi:Model"] ?? "gemini-3.1-flash-lite";
        _cache = cache;
        _logger = logger;
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    public async Task<JobExtractionResult> ExtractJobDetailsFromTextAsync(string text)
    {
        var key = $"gemini:text:{Hash(text)}";
        if (_cache.TryGetValue(key, out JobExtractionResult? cached) && cached != null) return cached;
        var prompt = BuildExtractionPrompt(text);
        var response = await CallGeminiAsync(prompt);
        var result = ParseExtractionResponse(response, text);
        if (result.IsSuccessful) _cache.Set(key, result, CacheTtl);
        return result;
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
        var key = $"gemini:url:{Hash(url)}";
        if (_cache.TryGetValue(key, out JobExtractionResult? cached) && cached != null) return cached;
        var truncated = htmlContent[..Math.Min(htmlContent.Length, 8000)];
        var prompt = $"Extract job details from this URL content ({url}). Return ONLY JSON (no markdown):\n" +
                     "{\"companyName\":\"\",\"role\":\"\",\"requiredSkills\":\"\",\"recruiterEmail\":null,\"experienceRequired\":null,\"location\":null}\n" +
                     "Rules: ignore nav/footer/ads; null if not found; never fabricate.\n\nContent:\n" + truncated;
        var response = await CallGeminiAsync(prompt);
        var result = ParseExtractionResponse(response, url);
        if (result.IsSuccessful) _cache.Set(key, result, CacheTtl);
        return result;
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
            var prompt = $"Is this a developer job requiring 3+ years experience? Reply TRUE or FALSE only.\n\n{postText[..Math.Min(postText.Length, 500)]}";
            var response = await CallGeminiAsync(prompt);
            return response.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filtering post via Gemini");
            return false;
        }
    }

    /// <summary>
    /// Batch relevance check — one Gemini call for N posts instead of N calls.
    /// Returns a bool per post in the same order.
    /// </summary>
    public async Task<List<bool>> BatchIsPostRelevantAsync(List<string> posts)
    {
        if (posts.Count == 0) return new List<bool>();
        try
        {
            var numbered = string.Join("\n---\n", posts.Select((p, i) =>
                $"[{i + 1}] {p[..Math.Min(p.Length, 400)]}"));
            var prompt = $@"For each numbered post below, reply TRUE if it's a developer job requiring 3+ years experience, FALSE otherwise.
Return ONLY a JSON array like [true,false,true] with exactly {posts.Count} values, no other text.

{numbered}";
            var response = await CallGeminiAsync(prompt);
            var cleaned = CleanJsonResponse(response);
            var results = JsonConvert.DeserializeObject<List<bool>>(cleaned);
            if (results != null && results.Count == posts.Count) return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch relevance check failed, falling back to individual calls.");
        }
        // Fallback: individual calls
        var fallback = new List<bool>();
        foreach (var p in posts) fallback.Add(await IsPostRelevantAsync(p));
        return fallback;
    }

    private string BuildExtractionPrompt(string text)
    {
        return "Extract job details. Return ONLY JSON (no markdown):\n" +
               "{\"companyName\":\"\",\"role\":\"\",\"requiredSkills\":\"\",\"recruiterEmail\":null,\"experienceRequired\":null,\"location\":null}\n" +
               "Rules: null if missing; never fabricate; list all skills.\n\nText:\n" + text;
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

    // Exposed for use by other services (e.g. CompanyEmailFinderService)
    public Task<string> CallGeminiPublicAsync(string prompt) => CallGeminiAsync(prompt);

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
        return await SendWithRateLimitAsync(url, json, "text");
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
        return await SendWithRateLimitAsync(url, json, mimeType);
    }

    /// <summary>
    /// Central HTTP sender with rate limiting (15 RPM) and exponential backoff retry.
    /// All Gemini calls go through this single method.
    /// </summary>
    private async Task<string> SendWithRateLimitAsync(string url, string jsonBody, string callType)
    {
        await _rateLimitGate.WaitAsync();
        try
        {
            // ── Enforce minimum gap between calls ────────────────────────
            var elapsed = (DateTime.UtcNow - _lastCallUtc).TotalMilliseconds;
            if (elapsed < MinGapMs)
            {
                var waitMs = (int)(MinGapMs - elapsed);
                _logger.LogInformation("Rate limiter: waiting {WaitMs}ms before next Gemini call.", waitMs);
                await Task.Delay(waitMs);
            }

            // ── Retry loop with exponential backoff ──────────────────────
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                _logger.LogInformation("Calling Gemini API ({CallType}){Retry}...",
                    callType, attempt > 0 ? $" [retry {attempt}/{MaxRetries}]" : "");

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.PostAsync(url, content);
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    // HTTP timeout — treat as retryable
                    _logger.LogWarning("Gemini API timeout on attempt {Attempt}.", attempt + 1);
                    if (attempt < MaxRetries)
                    {
                        var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                        await Task.Delay(delay);
                        continue;
                    }
                    throw;
                }

                _lastCallUtc = DateTime.UtcNow;

                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    return ExtractTextFromGeminiResponse(responseString);
                }

                // ── Retryable status codes: 429 (rate limit) and 503 (overloaded) ──
                if ((response.StatusCode == HttpStatusCode.TooManyRequests ||
                     response.StatusCode == HttpStatusCode.ServiceUnavailable) &&
                    attempt < MaxRetries)
                {
                    // Prefer Retry-After header from Google if available
                    var retryAfter = response.Headers.RetryAfter;
                    int delayMs;
                    if (retryAfter?.Delta != null)
                    {
                        delayMs = (int)retryAfter.Delta.Value.TotalMilliseconds;
                    }
                    else
                    {
                        delayMs = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                    }

                    _logger.LogWarning(
                        "Gemini API returned {StatusCode}. Retrying in {DelayMs}ms (attempt {Attempt}/{Max}).",
                        (int)response.StatusCode, delayMs, attempt + 1, MaxRetries);

                    await Task.Delay(delayMs);
                    continue;
                }

                // ── Non-retryable error ──────────────────────────────────
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Gemini API error: {StatusCode} - {Response}", response.StatusCode, errorBody);
                throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {errorBody}");
            }

            // Should not reach here, but just in case
            throw new HttpRequestException("Gemini API: all retry attempts exhausted.");
        }
        finally
        {
            _rateLimitGate.Release();
        }
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
