using System.Net;
using System.Net.Mail;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(bool Success, string? ErrorMessage)> SendEmailAsync(GeneratedEmail email, UserProfile profile, string? attachmentPath = null)
    {
        try
        {
            var smtpServer = _configuration["Gmail:SmtpServer"] ?? "smtp.gmail.com";
            var port = int.Parse(_configuration["Gmail:Port"] ?? "587");
            var senderEmail = _configuration["Gmail:SenderEmail"] ?? profile.Email;
            var appPassword = _configuration["Gmail:AppPassword"]
                ?? throw new InvalidOperationException("Gmail App Password is not configured");

            using var client = new SmtpClient(smtpServer, port)
            {
                Credentials = new NetworkCredential(senderEmail, appPassword),
                EnableSsl = true,
                Timeout = 30000
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(senderEmail, profile.FullName),
                Subject = email.Subject,
                Body = email.Body,
                IsBodyHtml = false
            };

            mailMessage.To.Add(new MailAddress(email.RecipientEmail));

            // Attach resume file if available
            if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            {
                var attachment = new Attachment(attachmentPath);
                mailMessage.Attachments.Add(attachment);
                _logger.LogInformation("Attached resume to email: {AttachmentPath}", attachmentPath);
            }

            _logger.LogInformation("Sending email to {Recipient} with subject: {Subject}",
                email.RecipientEmail, email.Subject);

            await client.SendMailAsync(mailMessage);

            _logger.LogInformation("Email sent successfully to {Recipient}", email.RecipientEmail);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipient}", email.RecipientEmail);
            return (false, ex.Message);
        }
    }
}
