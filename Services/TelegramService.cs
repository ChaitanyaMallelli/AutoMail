using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace JobAutomation.Services;

public class TelegramService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly ILogger<TelegramService> _logger;
    private readonly JobProcessingService _jobProcessingService;
    private readonly AppDbContext _dbContext;
    private readonly EmailService _emailService;
    private readonly GeminiService _geminiService;

    public TelegramService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TelegramService> logger,
        JobProcessingService jobProcessingService,
        AppDbContext dbContext,
        EmailService emailService,
        GeminiService geminiService)
    {
        _httpClient = httpClient;
        _botToken = configuration["Telegram:BotToken"] ?? "";
        _logger = logger;
        _jobProcessingService = jobProcessingService;
        _dbContext = dbContext;
        _emailService = emailService;
        _geminiService = geminiService;
    }

    public async Task ProcessUpdateAsync(JObject update)
    {
        long chatId = 0;
        try
        {
            // Check for Callback Query
            var callbackQuery = update["callback_query"];
            if (callbackQuery != null && callbackQuery.Type != JTokenType.Null)
            {
                await HandleCallbackQueryAsync(callbackQuery);
                return;
            }

            var message = update["message"];
            if (message == null) return;

            TelegramProgressTracker.RecordExecutionStart();
            chatId = (long?)message["chat"]?["id"] ?? 0;

            // Check for photo
            var photos = message["photo"] as JArray;
            if (photos != null && photos.Count > 0)
            {
                var photo = photos.Last();
                var fileId = photo?["file_id"]?.ToString();
                if (!string.IsNullOrEmpty(fileId))
                {
                    var imageBytes = await DownloadFileAsync(fileId);
                    if (imageBytes != null)
                    {
                        var jobId = await _jobProcessingService.ProcessImageAsync(imageBytes, "image/jpeg", Models.JobSource.Telegram, chatId);
                        await HandlePostExtractionFlowAsync(chatId, jobId);
                        return;
                    }
                }
            }

            // Check for document (PDF/Image)
            var document = message["document"];
            if (document != null)
            {
                var mimeType = document["mime_type"]?.ToString() ?? "";
                var fileId = document["file_id"]?.ToString();

                if (!string.IsNullOrEmpty(fileId))
                {
                    var fileBytes = await DownloadFileAsync(fileId);
                    if (fileBytes != null)
                    {
                        int jobId;
                        if (mimeType == "application/pdf")
                        {
                            jobId = await _jobProcessingService.ProcessPdfAsync(fileBytes, Models.JobSource.Telegram, chatId);
                        }
                        else
                        {
                            jobId = await _jobProcessingService.ProcessImageAsync(fileBytes, mimeType, Models.JobSource.Telegram, chatId);
                        }
                        await HandlePostExtractionFlowAsync(chatId, jobId);
                        return;
                    }
                }
            }

            // Check for text message or URL
            var text = message["text"]?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                if (text.StartsWith("/"))
                {
                    await HandleCommandAsync(chatId, text);
                    return;
                }

                // URL Detection
                if (JobProcessingService.ContainsUrl(text))
                {
                    var url = text.Trim();
                    var jobId = await _jobProcessingService.ProcessUrlAsync(url, Models.JobSource.Telegram, chatId);
                    await HandlePostExtractionFlowAsync(chatId, jobId);
                    return;
                }

                // Normal text post
                var textJobId = await _jobProcessingService.ProcessTextAsync(text, Models.JobSource.Telegram, chatId);
                await HandlePostExtractionFlowAsync(chatId, textJobId);
                return;
            }

            await SendReplyAsync(chatId, "⚠️ Please send a text job post, URL, screenshot, or PDF file.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram update");
            if (chatId != 0)
            {
                await SendReplyAsync(chatId, $"❌ <b>Processing Error:</b> {EscapeHtml(ex.Message)}");
            }
        }
    }

    private async Task HandlePostExtractionFlowAsync(long chatId, int jobId)
    {
        var job = await _dbContext.JobPosts.FindAsync(jobId);
        if (job == null) return;

        // Duplicate Check
        var duplicate = await _jobProcessingService.FindDuplicateAsync(job.CompanyName, job.Role);
        if (duplicate != null && duplicate.Id != jobId)
        {
            var inlineKeyboard = new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "✅ Apply Anyway", callback_data = $"duplicate_proceed:{jobId}" },
                        new { text = "❌ Skip", callback_data = $"duplicate_skip:{jobId}" }
                    }
                }
            };
            await SendKeyboardReplyAsync(chatId, $"⚠️ <b>Duplicate Found!</b>\nYou already applied to <b>{EscapeHtml(job.CompanyName)} - {EscapeHtml(job.Role)}</b> on {duplicate.CreatedAt:MMM dd} (Status: {duplicate.Status}).\n\nWhat would you like to do?", inlineKeyboard);
            return;
        }

        await ProceedToSmartFilterAsync(chatId, jobId);
    }

    private async Task ProceedToSmartFilterAsync(long chatId, int jobId)
    {
        var job = await _dbContext.JobPosts.FindAsync(jobId);
        if (job == null) return;

        if (job.SkillMatchPercentage < 30)
        {
            var inlineKeyboard = new
            {
                inline_keyboard = new[]
                {
                    new[] { new { text = "✅ Apply Anyway", callback_data = $"lowmatch_proceed:{jobId}" } },
                    new[] { new { text = "📝 Save for Later", callback_data = $"lowmatch_save:{jobId}" } },
                    new[] { new { text = "❌ Skip", callback_data = $"lowmatch_skip:{jobId}" } }
                }
            };
            await SendKeyboardReplyAsync(chatId, $"⚠️ <b>Low Match Alert!</b>\nYour skill match is only {job.SkillMatchPercentage}% for {EscapeHtml(job.Role)} at {EscapeHtml(job.CompanyName)}.\n\nDo you want to proceed?", inlineKeyboard);
            return;
        }

        await ProceedToToneSelectionAsync(chatId, jobId);
    }

    private async Task ProceedToToneSelectionAsync(long chatId, int jobId)
    {
        var inlineKeyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "💼 Professional", callback_data = $"tone_professional:{jobId}" },
                    new { text = "🔥 Enthusiastic", callback_data = $"tone_enthusiastic:{jobId}" },
                    new { text = "⚡ Concise", callback_data = $"tone_concise:{jobId}" }
                }
            }
        };
        await SendKeyboardReplyAsync(chatId, "🎨 <b>Select Email Tone</b>\nChoose the tone for your application email:", inlineKeyboard);
    }

    private async Task SendApprovalRequestAsync(long chatId, int jobId)
    {
        var job = await _dbContext.JobPosts.Include(j => j.GeneratedEmail).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job?.GeneratedEmail == null) return;

        TelegramProgressTracker.UpdateProgress(chatId, "Step 5: Awaiting approval... ⏳");

        var messageText = $"📝 <b>Email Ready! ({job.GeneratedEmail.Tone?.ToUpper()} Tone)</b>\n\n" +
                          $"🏢 <b>Company:</b> {EscapeHtml(job.CompanyName)}\n" +
                          $"💼 <b>Role:</b> {EscapeHtml(job.Role)}\n" +
                          $"🎯 <b>Match Score:</b> {job.SkillMatchPercentage}%\n\n" +
                          $"<b>Email Subject:</b> {EscapeHtml(job.GeneratedEmail.Subject)}\n\n" +
                          $"<b>Body:</b>\n<code>{EscapeHtml(job.GeneratedEmail.Body)}</code>\n\n" +
                          $"<i>Approve and send?</i>";

        var inlineKeyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "✅ Approve & Send", callback_data = $"approve_send:{jobId}" },
                    new { text = "❌ Reject", callback_data = $"reject:{jobId}" }
                }
            }
        };

        await SendKeyboardReplyAsync(chatId, messageText, inlineKeyboard);
    }

    private async Task HandleCallbackQueryAsync(JToken callbackQuery)
    {
        long chatId = 0, messageId = 0;
        try
        {
            var queryId = callbackQuery["id"]?.ToString();
            var messageToken = callbackQuery["message"];
            if (messageToken != null && messageToken.Type != JTokenType.Null)
            {
                chatId = messageToken["chat"]?["id"]?.Value<long>() ?? 0;
                messageId = messageToken["message_id"]?.Value<long>() ?? 0;
            }
            
            var data = callbackQuery["data"]?.ToString() ?? "";

            // Answer callback query immediately
            if (!string.IsNullOrEmpty(queryId))
            {
                try { await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/answerCallbackQuery?callback_query_id={queryId}", new StringContent("", Encoding.UTF8, "application/json")); } catch { }
            }

            var parts = data.Split(':');
            if (parts.Length < 2) return;
            var action = parts[0];
            if (!int.TryParse(parts[1], out var jobId)) return;

            var job = await _dbContext.JobPosts.Include(j => j.GeneratedEmail).FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null) return;

            switch (action)
            {
                // Duplicate handling
                case "duplicate_proceed":
                    await EditMessageTextAsync(chatId, messageId, "✅ Proceeding with application...");
                    await ProceedToSmartFilterAsync(chatId, jobId);
                    break;
                case "duplicate_skip":
                    job.Status = JobStatus.Skipped;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "❌ Job skipped.");
                    break;

                // Low match handling
                case "lowmatch_proceed":
                    await EditMessageTextAsync(chatId, messageId, "✅ Proceeding despite low match...");
                    await ProceedToToneSelectionAsync(chatId, jobId);
                    break;
                case "lowmatch_save":
                    job.Status = JobStatus.SavedForLater;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "📝 Saved to dashboard for later review.");
                    break;
                case "lowmatch_skip":
                    job.Status = JobStatus.Skipped;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "❌ Job skipped due to low match.");
                    break;

                // Tone selection
                case "tone_professional":
                case "tone_enthusiastic":
                case "tone_concise":
                    var tone = action.Replace("tone_", "");
                    await EditMessageTextAsync(chatId, messageId, $"⏳ Generating email with {tone} tone...");
                    await _jobProcessingService.ContinueProcessingAsync(jobId, tone);
                    await SendApprovalRequestAsync(chatId, jobId);
                    break;

                // Approval handling
                case "approve_send":
                    await ProcessApprovalAndSend(chatId, messageId, job);
                    break;
                case "reject":
                    job.Status = JobStatus.Failed;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "❌ Email draft rejected.");
                    break;

                // Follow-up flow
                case "followup_send":
                    await EditMessageTextAsync(chatId, messageId, "⏳ Generating follow-up email...");
                    var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();
                    if (profile != null)
                    {
                        var draft = await _geminiService.GenerateFollowUpEmailAsync(job, profile);
                        if (draft.IsSuccessful)
                        {
                            var generatedFollowUp = new GeneratedEmail { JobPostId = job.Id, Subject = draft.Subject, Body = draft.Body, RecipientEmail = job.RecruiterEmail ?? "", Tone = "followup" };
                            var (success, err) = await _emailService.SendEmailAsync(generatedFollowUp, profile, null);
                            if (success)
                            {
                                job.Status = JobStatus.FollowUpSent;
                                await _dbContext.SaveChangesAsync();
                                await EditMessageTextAsync(chatId, messageId, "🚀 Follow-up email sent successfully!");
                                await PromptForResponseTracking(chatId, job.Id);
                            }
                            else
                            {
                                await EditMessageTextAsync(chatId, messageId, $"❌ Failed to send follow-up: {err}");
                            }
                        }
                    }
                    break;
                case "followup_gotresponse":
                    await EditMessageTextAsync(chatId, messageId, "Awesome! Did you get an interview?");
                    await PromptForResponseTracking(chatId, job.Id);
                    break;
                case "followup_dismiss":
                    await EditMessageTextAsync(chatId, messageId, "Dismissed reminder.");
                    break;

                // Response Tracking
                case "response_interview":
                    job.Status = JobStatus.InterviewScheduled;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "🎉 Awesome! Marked as Interview Scheduled. Good luck!");
                    break;
                case "response_rejected":
                    job.Status = JobStatus.Rejected;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "😞 Sorry to hear that. Marked as Rejected. Keep trying!");
                    break;
                case "response_ghosted":
                    job.Status = JobStatus.Ghosted;
                    await _dbContext.SaveChangesAsync();
                    await EditMessageTextAsync(chatId, messageId, "👻 Marked as Ghosted.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling callback query");
        }
    }

    private async Task ProcessApprovalAndSend(long chatId, long messageId, JobPost job)
    {
        await EditMessageTextAsync(chatId, messageId, "⏳ Sending email...");
        
        string? attachmentPath = null;
        var activeResume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
        if (activeResume != null && !string.IsNullOrEmpty(activeResume.FilePath))
        {
            if (activeResume.FilePath.StartsWith("Resume", StringComparison.OrdinalIgnoreCase))
                attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), activeResume.FilePath.Replace("/", "\\"));
            else
                attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", activeResume.FilePath.TrimStart('/'));
        }

        var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();
        if (profile == null || job.GeneratedEmail == null) return;

        var (success, errorMessage) = await _emailService.SendEmailAsync(job.GeneratedEmail, profile, attachmentPath);

        if (success)
        {
            job.GeneratedEmail.IsSent = true;
            job.GeneratedEmail.SentAt = DateTime.UtcNow;
            job.Status = JobStatus.Sent;
            await _dbContext.SaveChangesAsync();
            await EditMessageTextAsync(chatId, messageId, $"🚀 <b>Email Sent Successfully!</b>\nTo: {EscapeHtml(job.GeneratedEmail.RecipientEmail)}");
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.GeneratedEmail.ErrorMessage = errorMessage;
            await _dbContext.SaveChangesAsync();
            await EditMessageTextAsync(chatId, messageId, $"❌ <b>Failed to Send Email:</b>\n<code>{EscapeHtml(errorMessage)}</code>");
        }
    }

    private async Task PromptForResponseTracking(long chatId, int jobId)
    {
        var inlineKeyboard = new
        {
            inline_keyboard = new[]
            {
                new[] { new { text = "📞 Interview Scheduled", callback_data = $"response_interview:{jobId}" } },
                new[] { new { text = "❌ Rejected", callback_data = $"response_rejected:{jobId}" } },
                new[] { new { text = "👻 Ghosted / No Response", callback_data = $"response_ghosted:{jobId}" } }
            }
        };
        await SendKeyboardReplyAsync(chatId, "Any updates on this application?", inlineKeyboard);
    }

    private async Task HandleCommandAsync(long chatId, string command)
    {
        switch (command.ToLower().Split('@')[0])
        {
            case "/start":
            case "/help":
                await SendReplyAsync(chatId, "👋 Welcome to JobFlow AI!\n\nSend me:\n🔗 Job URLs (LinkedIn, Indeed)\n📝 Text job posts\n📸 Screenshots\n📄 PDF job descriptions\n\nI'll extract details, match your resume, and write the email!");
                break;
        }
    }

    public async Task<byte[]?> DownloadFileAsync(string fileId)
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"https://api.telegram.org/bot{_botToken}/getFile?file_id={fileId}");
            var filePath = JObject.Parse(response)["result"]?["file_path"]?.ToString();
            if (string.IsNullOrEmpty(filePath)) return null;
            return await _httpClient.GetByteArrayAsync($"https://api.telegram.org/file/bot{_botToken}/{filePath}");
        }
        catch { return null; }
    }

    private string EscapeHtml(string? text) => string.IsNullOrEmpty(text) ? "" : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public async Task SendReplyAsync(long chatId, string message)
    {
        try { await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/sendMessage", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("chat_id", chatId.ToString()), new KeyValuePair<string, string>("text", message), new KeyValuePair<string, string>("parse_mode", "HTML") })); } catch { }
    }

    public async Task SendKeyboardReplyAsync(long chatId, string message, object replyMarkup)
    {
        try { await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/sendMessage", new StringContent(JsonConvert.SerializeObject(new { chat_id = chatId, text = message, parse_mode = "HTML", reply_markup = replyMarkup }), Encoding.UTF8, "application/json")); } catch { }
    }

    public async Task EditMessageTextAsync(long chatId, long messageId, string newText)
    {
        try { await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/editMessageText", new StringContent(JsonConvert.SerializeObject(new { chat_id = chatId, message_id = messageId, text = newText, parse_mode = "HTML" }), Encoding.UTF8, "application/json")); } catch { }
    }

    public async Task<JObject?> GetBotInfoAsync() { try { return JObject.Parse(await _httpClient.GetStringAsync($"https://api.telegram.org/bot{_botToken}/getMe")); } catch { return null; } }
    public async Task<JObject?> GetWebhookInfoAsync() { try { return JObject.Parse(await _httpClient.GetStringAsync($"https://api.telegram.org/bot{_botToken}/getWebhookInfo")); } catch { return null; } }
    public async Task<bool> SetWebhookAsync(string url) { try { return JObject.Parse(await _httpClient.GetStringAsync($"https://api.telegram.org/bot{_botToken}/setWebhook?url={Uri.EscapeDataString(url)}&allowed_updates={Uri.EscapeDataString(JsonConvert.SerializeObject(new[] { "message", "callback_query" }))}"))["ok"]?.Value<bool>() ?? false; } catch { return false; } }
    public async Task<bool> DeleteWebhookAsync() { try { return JObject.Parse(await _httpClient.GetStringAsync($"https://api.telegram.org/bot{_botToken}/deleteWebhook"))["ok"]?.Value<bool>() ?? false; } catch { return false; } }
}
