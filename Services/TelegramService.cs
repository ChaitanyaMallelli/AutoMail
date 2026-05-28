using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using System.IO;
using System.Text;

namespace JobAutomation.Services;

public class TelegramService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly ILogger<TelegramService> _logger;
    private readonly JobProcessingService _jobProcessingService;
    private readonly AppDbContext _dbContext;
    private readonly EmailService _emailService;

    public TelegramService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<TelegramService> logger,
        JobProcessingService jobProcessingService,
        AppDbContext dbContext,
        EmailService emailService)
    {
        _httpClient = httpClient;
        _botToken = configuration["Telegram:BotToken"] ?? "";
        _logger = logger;
        _jobProcessingService = jobProcessingService;
        _dbContext = dbContext;
        _emailService = emailService;
    }

    public async Task ProcessUpdateAsync(JObject update)
    {
        long chatId = 0;
        try
        {
            var keys = update.Properties().Select(p => p.Name).ToList();
            _logger.LogInformation("ProcessUpdateAsync: Received Telegram update. Root properties: {Properties}", string.Join(", ", keys));

            // Check for Callback Query
            var callbackQuery = update["callback_query"];
            if (callbackQuery != null && callbackQuery.Type != JTokenType.Null)
            {
                _logger.LogInformation("ProcessUpdateAsync: Detected 'callback_query' in update body. Routing to HandleCallbackQueryAsync.");
                await HandleCallbackQueryAsync(callbackQuery);
                return;
            }

            var message = update["message"];
            if (message == null)
            {
                _logger.LogWarning("Received update without message");
                return;
            }

            TelegramProgressTracker.RecordExecutionStart();

            chatId = (long?)message["chat"]?["id"] ?? 0;

            // Check for photo
            var photos = message["photo"] as JArray;
            if (photos != null && photos.Count > 0)
            {
                // Get the highest resolution photo
                var photo = photos.Last();
                var fileId = photo?["file_id"]?.ToString();
                if (!string.IsNullOrEmpty(fileId))
                {
                    _logger.LogInformation("Processing Telegram photo from chat {ChatId}", chatId);
                    var imageBytes = await DownloadFileAsync(fileId);
                    if (imageBytes != null)
                    {
                        var jobId = await _jobProcessingService.ProcessImageAsync(imageBytes, "image/jpeg", Models.JobSource.Telegram, chatId);
                        await SendApprovalRequestAsync(chatId, jobId);
                        return;
                    }
                }
            }

            // Check for document (PDF)
            var document = message["document"];
            if (document != null)
            {
                var mimeType = document["mime_type"]?.ToString() ?? "";
                var fileId = document["file_id"]?.ToString();

                if (mimeType == "application/pdf" && !string.IsNullOrEmpty(fileId))
                {
                    _logger.LogInformation("Processing Telegram PDF from chat {ChatId}", chatId);
                    var pdfBytes = await DownloadFileAsync(fileId);
                    if (pdfBytes != null)
                    {
                        var jobId = await _jobProcessingService.ProcessPdfAsync(pdfBytes, Models.JobSource.Telegram, chatId);
                        await SendApprovalRequestAsync(chatId, jobId);
                        return;
                    }
                }
                // Image sent as document
                else if (mimeType.StartsWith("image/") && !string.IsNullOrEmpty(fileId))
                {
                    var imageBytes = await DownloadFileAsync(fileId);
                    if (imageBytes != null)
                    {
                        var jobId = await _jobProcessingService.ProcessImageAsync(imageBytes, mimeType, Models.JobSource.Telegram, chatId);
                        await SendApprovalRequestAsync(chatId, jobId);
                        return;
                    }
                }
            }

            // Check for text message
            var text = message["text"]?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                // Skip bot commands
                if (text.StartsWith("/"))
                {
                    await HandleCommandAsync(chatId, text);
                    return;
                }

                // Check for manual approval or rejection text command
                var cleanText = text.Trim().ToLower().Replace("&", "and");
                if (cleanText == "approve" || cleanText == "approve and send" || cleanText == "reject")
                {
                    _logger.LogInformation("ProcessUpdateAsync: Intercepted manual text confirmation command: {Text}", text);
                    
                    var latestJob = await _dbContext.JobPosts
                        .Include(j => j.GeneratedEmail)
                        .Where(j => j.Status == JobStatus.EmailGenerated || j.Status == JobStatus.Pending)
                        .OrderByDescending(j => j.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (latestJob != null && latestJob.GeneratedEmail != null)
                    {
                        var action = cleanText.Contains("approve") ? "approve_send" : "reject";
                        var fakeCallbackQuery = new JObject
                        {
                            ["id"] = "manual_text_trigger",
                            ["message"] = new JObject
                            {
                                ["message_id"] = 0, // 0 message_id indicates manual text trigger
                                ["chat"] = new JObject
                                {
                                    ["id"] = chatId
                                }
                            },
                            ["data"] = $"{action}:{latestJob.Id}"
                        };

                        _logger.LogInformation("ProcessUpdateAsync: Routing manual text command to HandleCallbackQueryAsync for JobPostId {JobId}.", latestJob.Id);
                        await HandleCallbackQueryAsync(fakeCallbackQuery);
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("ProcessUpdateAsync: Manual text confirmation received, but no pending job post with generated email was found.");
                    }
                }

                _logger.LogInformation("Processing Telegram text from chat {ChatId}", chatId);
                var jobId = await _jobProcessingService.ProcessTextAsync(text, Models.JobSource.Telegram, chatId);
                await SendApprovalRequestAsync(chatId, jobId);
                return;
            }

            await SendReplyAsync(chatId, "⚠️ Please send a text job post, screenshot, or PDF file.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram update");
            if (chatId != 0)
            {
                var friendlyMessage = ex.Message.Contains("ServiceUnavailable")
                    ? "❌ <b>Gemini AI is currently overloaded.</b> Google's Gemini 2.5 Flash model is experiencing high demand. Please try again in a few moments."
                    : $"❌ <b>Processing Error:</b> {ex.Message}";

                await SendReplyAsync(chatId, friendlyMessage);
            }
        }
    }

    private async Task SendApprovalRequestAsync(long chatId, int jobId)
    {
        _logger.LogInformation("SendApprovalRequestAsync: Preparing approval request for JobPostId {JobId} to ChatId {ChatId}", jobId, chatId);

        // Fetch JobPost and GeneratedEmail
        var job = await _dbContext.JobPosts
            .Include(j => j.GeneratedEmail)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job == null)
        {
            _logger.LogWarning("SendApprovalRequestAsync: JobPostId {JobId} not found in database", jobId);
            TelegramProgressTracker.UpdateProgress(chatId, "Step 5: Error resolving job post ❌");
            await SendReplyAsync(chatId, "⚠️ Error: Job post was not saved successfully.");
            return;
        }

        if (job.GeneratedEmail == null)
        {
            _logger.LogInformation("SendApprovalRequestAsync: No generated email for JobPostId {JobId}. Sending details card without action buttons.", jobId);
            TelegramProgressTracker.UpdateProgress(chatId, "Step 5: Process complete! Checked dashboard (Ready for review) ✅");
            await SendReplyAsync(chatId, $"✅ <b>Details Extracted!</b>\n\n🏢 <b>Company:</b> {EscapeHtml(job.CompanyName)}\n💼 <b>Role:</b> {EscapeHtml(job.Role)}\n🎯 <b>Match Score:</b> {job.SkillMatchPercentage}%\n\nNo email draft generated (check configured resume/profile on dashboard).");
            return;
        }

        TelegramProgressTracker.UpdateProgress(chatId, "Step 5: Awaiting approval... ⏳");

        var messageText = $"📝 <b>New Job Application Ready!</b>\n\n" +
                          $"🏢 <b>Company:</b> {EscapeHtml(job.CompanyName)}\n" +
                          $"💼 <b>Role:</b> {EscapeHtml(job.Role)}\n" +
                          $"🎯 <b>Match Score:</b> {job.SkillMatchPercentage}%\n" +
                          $"📊 <b>ATS Score:</b> {job.AtsScore}%\n" +
                          $"✉️ <b>Recipient:</b> {EscapeHtml(job.RecruiterEmail ?? "Not specified")}\n\n" +
                          $"<b>Email Subject:</b> {EscapeHtml(job.GeneratedEmail.Subject)}\n\n" +
                          $"<b>Email Context (Body):</b>\n" +
                          $"<code>{EscapeHtml(job.GeneratedEmail.Body)}</code>\n\n" +
                          $"<i>Would you like to approve and send this email?</i>";

        // Create Inline Keyboard Markup
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

        _logger.LogInformation("SendApprovalRequestAsync: Sending inline keyboard for JobPostId {JobId}", jobId);
        await SendKeyboardReplyAsync(chatId, messageText, inlineKeyboard);
        TelegramProgressTracker.UpdateProgress(chatId, "Step 5: Approval request sent! Awaiting decision on Telegram... ⏳");
    }

    private async Task HandleCallbackQueryAsync(JToken callbackQuery)
    {
        long chatId = 0;
        long messageId = 0;
        try
        {
            var queryId = callbackQuery["id"]?.ToString();
            
            var messageToken = callbackQuery["message"];
            if (messageToken != null && messageToken.Type != JTokenType.Null)
            {
                var chatToken = messageToken["chat"];
                if (chatToken != null && chatToken.Type != JTokenType.Null)
                {
                    chatId = chatToken["id"]?.Value<long>() ?? 0;
                }
                messageId = messageToken["message_id"]?.Value<long>() ?? 0;
            }
            
            var data = callbackQuery["data"]?.ToString() ?? "";

            _logger.LogInformation("HandleCallbackQueryAsync: Received Callback Query. QueryId: {QueryId}, ChatId: {ChatId}, MessageId: {MessageId}, Data: {Data}", queryId, chatId, messageId, data);
            _logger.LogInformation("HandleCallbackQueryAsync: Raw query payload: {Payload}", callbackQuery.ToString(Formatting.None));

            // Answer callback query immediately so Telegram button stops spinning
            if (queryId != "manual_text_trigger" && !string.IsNullOrEmpty(queryId))
            {
                try
                {
                    var answerUrl = $"https://api.telegram.org/bot{_botToken}/answerCallbackQuery?callback_query_id={queryId}";
                    _logger.LogInformation("HandleCallbackQueryAsync: Answering callback query to URL: {Url}", answerUrl);
                    var answerResponse = await _httpClient.PostAsync(answerUrl, new StringContent("", Encoding.UTF8, "application/json"));
                    if (!answerResponse.IsSuccessStatusCode)
                    {
                        var answerErr = await answerResponse.Content.ReadAsStringAsync();
                        _logger.LogWarning("HandleCallbackQueryAsync: answerCallbackQuery API returned status {Status}. Body: {Body}", answerResponse.StatusCode, answerErr);
                    }
                    else
                    {
                        _logger.LogInformation("HandleCallbackQueryAsync: Callback query answered successfully.");
                    }
                }
                catch (Exception answerEx)
                {
                    _logger.LogError(answerEx, "HandleCallbackQueryAsync: Failed to answer callback query");
                }
            }

            var parts = data.Split(':');
            if (parts.Length < 2)
            {
                _logger.LogWarning("HandleCallbackQueryAsync: Callback data had invalid format (no colon): {Data}", data);
                return;
            }

            var action = parts[0];
            if (!int.TryParse(parts[1], out var jobId))
            {
                _logger.LogWarning("HandleCallbackQueryAsync: Callback data had invalid non-integer JobId: {JobIdStr}", parts[1]);
                return;
            }

            _logger.LogInformation("HandleCallbackQueryAsync: Parsed action '{Action}' for JobPostId {JobId}", action, jobId);

            var job = await _dbContext.JobPosts
                .Include(j => j.GeneratedEmail)
                .FirstOrDefaultAsync(j => j.Id == jobId);

            if (job == null)
            {
                _logger.LogWarning("HandleCallbackQueryAsync: JobPostId {JobId} not found in database", jobId);
                var errTxt = "⚠️ Error: Job post not found.";
                if (messageId > 0) await EditMessageTextAsync(chatId, messageId, errTxt);
                else await SendReplyAsync(chatId, errTxt);
                return;
            }

            if (job.GeneratedEmail == null)
            {
                _logger.LogWarning("HandleCallbackQueryAsync: Generated email not found for JobPostId {JobId}", jobId);
                var errTxt = "⚠️ Error: Email draft not found.";
                if (messageId > 0) await EditMessageTextAsync(chatId, messageId, errTxt);
                else await SendReplyAsync(chatId, errTxt);
                return;
            }

            var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();
            if (profile == null)
            {
                _logger.LogWarning("HandleCallbackQueryAsync: User profile is not configured in database.");
                var errTxt = "⚠️ Error: User profile not configured.";
                if (messageId > 0) await EditMessageTextAsync(chatId, messageId, errTxt);
                else await SendReplyAsync(chatId, errTxt);
                return;
            }

            if (action == "approve_send")
            {
                _logger.LogInformation("HandleCallbackQueryAsync: User approved email sending for JobPostId {JobId}", jobId);
                TelegramProgressTracker.UpdateProgress(chatId, "Step 6: User approved email! Sending... ⏳");
                var procTxt = "⏳ <b>Processing...</b> Sending email to recruiter...";
                if (messageId > 0) await EditMessageTextAsync(chatId, messageId, procTxt);
                else await SendReplyAsync(chatId, procTxt);

                // Resolve attachment path
                string? attachmentPath = null;
                var folderResumePath = Path.Combine(Directory.GetCurrentDirectory(), "Resume", "Chaitanya_Mallelli_Resume.pdf");
                var specificPath = @"C:\Users\Susmita sahoo\Downloads\Chaitanya_Mallelli_Resume.pdf";

                if (System.IO.File.Exists(folderResumePath))
                {
                    attachmentPath = folderResumePath;
                    _logger.LogInformation("HandleCallbackQueryAsync: Using project-folder relative resume: {Path}", folderResumePath);
                }
                else if (System.IO.File.Exists(specificPath))
                {
                    attachmentPath = specificPath;
                    _logger.LogInformation("HandleCallbackQueryAsync: Using absolute local resume: {Path}", specificPath);
                }
                else
                {
                    var activeResume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
                    if (activeResume != null && !string.IsNullOrEmpty(activeResume.FilePath))
                    {
                        if (activeResume.FilePath.StartsWith("Resume", StringComparison.OrdinalIgnoreCase))
                        {
                            attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), activeResume.FilePath.Replace("/", "\\"));
                        }
                        else
                        {
                            attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", activeResume.FilePath.TrimStart('/'));
                        }
                        _logger.LogInformation("HandleCallbackQueryAsync: Using active database resume path: {Path}", attachmentPath);
                    }
                    else
                    {
                        _logger.LogWarning("HandleCallbackQueryAsync: No active resume found in database or relative/absolute fallback files!");
                    }
                }

                _logger.LogInformation("HandleCallbackQueryAsync: Triggering SMTP SendEmailAsync...");
                var (success, errorMessage) = await _emailService.SendEmailAsync(job.GeneratedEmail, profile, attachmentPath);

                if (success)
                {
                    _logger.LogInformation("HandleCallbackQueryAsync: Email sent successfully for JobPostId {JobId}!", jobId);
                    job.GeneratedEmail.IsSent = true;
                    job.GeneratedEmail.SentAt = DateTime.UtcNow;
                    job.Status = JobStatus.Sent;
                    await _dbContext.SaveChangesAsync();

                    TelegramProgressTracker.UpdateProgress(chatId, "Step 6: Email sent successfully! 🎉 ✅");

                    var successText = $"🚀 <b>Email Sent Successfully!</b>\n\n" +
                                      $"🏢 <b>Company:</b> {EscapeHtml(job.CompanyName)}\n" +
                                      $"💼 <b>Role:</b> {EscapeHtml(job.Role)}\n" +
                                      $"✉️ <b>Sent To:</b> {EscapeHtml(job.GeneratedEmail.RecipientEmail)}\n\n" +
                                      $"✅ Application is fully completed! Check your web dashboard for tracking.";

                    if (messageId > 0) await EditMessageTextAsync(chatId, messageId, successText);
                    else await SendReplyAsync(chatId, successText);
                }
                else
                {
                    _logger.LogError("HandleCallbackQueryAsync: SMTP Send failed: {Error}", errorMessage);
                    job.Status = JobStatus.Failed;
                    job.GeneratedEmail.ErrorMessage = errorMessage;
                    await _dbContext.SaveChangesAsync();

                    TelegramProgressTracker.UpdateProgress(chatId, $"Step 6: Email failed to send ❌ ({errorMessage})");

                    var failText = $"❌ <b>Failed to Send Email:</b>\n" +
                                   $"<code>{EscapeHtml(errorMessage)}</code>\n\n" +
                                   $"You can retry sending this email from your web dashboard.";

                    if (messageId > 0) await EditMessageTextAsync(chatId, messageId, failText);
                    else await SendReplyAsync(chatId, failText);
                }
            }
            else if (action == "reject")
            {
                _logger.LogInformation("HandleCallbackQueryAsync: User rejected email for JobPostId {JobId}", jobId);
                job.Status = JobStatus.Failed;
                await _dbContext.SaveChangesAsync();

                TelegramProgressTracker.UpdateProgress(chatId, "Step 6: Email draft rejected by user ❌");

                var rejectText = $"❌ <b>Job Application Rejected</b>\n\n" +
                                 $"🏢 <b>Company:</b> {EscapeHtml(job.CompanyName)}\n" +
                                 $"💼 <b>Role:</b> {EscapeHtml(job.Role)}\n\n" +
                                 $"The email draft was saved but marked as rejected. You can still view, edit, or send it from your web dashboard.";

                if (messageId > 0) await EditMessageTextAsync(chatId, messageId, rejectText);
                else await SendReplyAsync(chatId, rejectText);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling callback query");
            if (chatId != 0)
            {
                try
                {
                    await SendReplyAsync(chatId, $"❌ <b>Approval Error:</b> {EscapeHtml(ex.Message)}");
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "Error sending failure notification back to Telegram");
                }
            }
        }
    }

    private async Task HandleCommandAsync(long chatId, string command)
    {
        switch (command.ToLower().Split('@')[0])
        {
            case "/start":
                await SendReplyAsync(chatId, "👋 Welcome to Job Application Bot!\n\nSend me:\n📝 Text job posts\n📸 Screenshots of job listings\n📄 PDF job descriptions\n\nI'll extract the details, match with your resume, and generate a professional email for you!");
                break;
            case "/help":
                await SendReplyAsync(chatId, "📋 How to use:\n\n1. Send a job post text or screenshot\n2. I'll extract company, role, skills, etc.\n3. Check your dashboard to review & send the email\n\nSupported formats:\n- Text messages\n- Images/Screenshots\n- PDF files");
                break;
            default:
                await SendReplyAsync(chatId, "Unknown command. Try /start or /help");
                break;
        }
    }

    public async Task<byte[]?> DownloadFileAsync(string fileId)
    {
        try
        {
            // Get file path from Telegram
            var fileInfoUrl = $"https://api.telegram.org/bot{_botToken}/getFile?file_id={fileId}";
            var response = await _httpClient.GetStringAsync(fileInfoUrl);
            var fileInfo = JObject.Parse(response);
            var filePath = fileInfo["result"]?["file_path"]?.ToString();

            if (string.IsNullOrEmpty(filePath))
            {
                _logger.LogWarning("Could not get file path for file_id: {FileId}", fileId);
                return null;
            }

            // Download the file
            var downloadUrl = $"https://api.telegram.org/file/bot{_botToken}/{filePath}";
            return await _httpClient.GetByteArrayAsync(downloadUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file from Telegram: {FileId}", fileId);
            return null;
        }
    }

    private string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    public async Task SendReplyAsync(long chatId, string message)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", chatId.ToString()),
                new KeyValuePair<string, string>("text", message),
                new KeyValuePair<string, string>("parse_mode", "HTML")
            });
            _logger.LogInformation("SendReplyAsync: Sending text reply to ChatId {ChatId}", chatId);
            var response = await _httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("SendReplyAsync failed. Status: {Status}, Body: {Body}", response.StatusCode, errBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram reply to chat {ChatId}", chatId);
        }
    }

    public async Task SendKeyboardReplyAsync(long chatId, string message, object replyMarkup)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var requestBody = new
            {
                chat_id = chatId,
                text = message,
                parse_mode = "HTML",
                reply_markup = replyMarkup
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _logger.LogInformation("SendKeyboardReplyAsync: Sending keyboard reply to ChatId {ChatId}", chatId);
            var response = await _httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("SendKeyboardReplyAsync failed. Status: {Status}, Body: {Body}", response.StatusCode, errBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send keyboard reply to chat {ChatId}", chatId);
        }
    }

    public async Task EditMessageTextAsync(long chatId, long messageId, string newText)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/editMessageText";
            var requestBody = new
            {
                chat_id = chatId,
                message_id = messageId,
                text = newText,
                parse_mode = "HTML"
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _logger.LogInformation("EditMessageTextAsync: Editing message {MessageId} in ChatId {ChatId}", messageId, chatId);
            var response = await _httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("EditMessageTextAsync failed. Status: {Status}, Body: {Body}", response.StatusCode, errBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit message {MessageId} in chat {ChatId}", messageId, chatId);
        }
    }

    public async Task<JObject?> GetBotInfoAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_botToken)) return null;
            var url = $"https://api.telegram.org/bot{_botToken}/getMe";
            var response = await _httpClient.GetStringAsync(url);
            return JObject.Parse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Telegram bot info");
            return null;
        }
    }

    public async Task<JObject?> GetWebhookInfoAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_botToken)) return null;
            var url = $"https://api.telegram.org/bot{_botToken}/getWebhookInfo";
            var response = await _httpClient.GetStringAsync(url);
            return JObject.Parse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Telegram webhook info");
            return null;
        }
    }

    public async Task<bool> SetWebhookAsync(string webhookUrl)
    {
        try
        {
            if (string.IsNullOrEmpty(_botToken)) return false;
            var allowedUpdatesList = new[] { "message", "callback_query" };
            var allowedUpdatesJson = JsonConvert.SerializeObject(allowedUpdatesList);
            var url = $"https://api.telegram.org/bot{_botToken}/setWebhook?url={Uri.EscapeDataString(webhookUrl)}&allowed_updates={Uri.EscapeDataString(allowedUpdatesJson)}";
            var response = await _httpClient.GetStringAsync(url);
            var result = JObject.Parse(response);
            return result["ok"]?.Value<bool>() ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set Telegram webhook to {WebhookUrl}", webhookUrl);
            return false;
        }
    }

    public async Task<bool> DeleteWebhookAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_botToken)) return false;
            var url = $"https://api.telegram.org/bot{_botToken}/deleteWebhook";
            var response = await _httpClient.GetStringAsync(url);
            var result = JObject.Parse(response);
            return result["ok"]?.Value<bool>() ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Telegram webhook");
            return false;
        }
    }
}
