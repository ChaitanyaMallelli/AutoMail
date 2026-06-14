using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Services;
using JobAutomation.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddMemoryCache();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<JobAutomation.Filters.PasscodeAuthFilter>();
});

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// HTTP Client
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddHttpClient<TelegramService>();

// Application Services
builder.Services.AddScoped<GeminiService>();
builder.Services.AddScoped<ResumeMatchingService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<JobProcessingService>();
builder.Services.AddScoped<TelegramService>();
// Job board scrapers — only LinkedIn active; Naukri/Indeed kept in codebase but disabled
builder.Services.AddScoped<LinkedInScraperService>();
builder.Services.AddScoped<IJobBoardScraper, LinkedInScraperService>();
builder.Services.AddScoped<CompanyEmailFinderService>();
builder.Services.AddScoped<DirectApplyService>();
builder.Services.AddScoped<AutoApplyService>();
builder.Services.AddScoped<JobScoutManager>();

// builder.Services.AddSignalR(); // Temporarily disabled Co-Pilot

// Hosted Background Services
builder.Services.AddHostedService<JobAutomation.Workers.FollowUpBackgroundService>();
builder.Services.AddHostedService<JobAutomation.Workers.JobScoutBackgroundService>();
builder.Services.AddHostedService<JobAutomation.Workers.GmailReplyMonitorService>();
builder.Services.AddHostedService<JobAutomation.Workers.DailyReportBackgroundService>();

// JSON options
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
    });

var app = builder.Build();

// Ensure database is created and migrations are applied
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        // Manual Schema Evolution for recently added columns
        // Since GenerateCreateScript only handles creating non-existent tables, we need to alter existing ones
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"JobPosts\" ADD COLUMN \"TelegramChatId\" bigint NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"JobPosts\" ADD COLUMN \"FollowUpReminderSent\" boolean NOT NULL DEFAULT FALSE;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"JobPosts\" ADD COLUMN \"ResponseNotes\" character varying(2000) NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"Tone\" character varying(50) NULL DEFAULT 'professional';"); } catch { }
        // Feature 9: Email Open Tracking columns
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"TrackingToken\" uuid NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"OpenedAt\" timestamp with time zone NULL;"); } catch { }
        // Feature 8: Gmail Reply Detection columns
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"MessageId\" character varying(500) NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"RepliedAt\" timestamp with time zone NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"ReplySubject\" character varying(200) NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"ReplySnippet\" character varying(1000) NULL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GeneratedEmails\" ADD COLUMN \"ReplyClassification\" character varying(50) NULL;"); } catch { }
        // Persist Telegram chat ID on UserProfile so it survives job data clears
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"UserProfiles\" ADD COLUMN \"TelegramChatId\" bigint NULL;"); } catch { }
        // Performance indexes
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_scouted_jobs_url ON \"ScoutedJobs\" (\"LinkedInUrl\");"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_scouted_jobs_status ON \"ScoutedJobs\" (\"Status\");"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_job_posts_created ON \"JobPosts\" (\"CreatedAt\" DESC);"); } catch { }
        // Multi-board scout: track which board each scouted job came from
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"ScoutedJobs\" ADD COLUMN \"Board\" character varying(50) NOT NULL DEFAULT 'LinkedIn';"); } catch { }
        // Link manually placed resume file to active resume record if FilePath is missing
        try { db.Database.ExecuteSqlRaw("UPDATE \"Resumes\" SET \"FilePath\" = 'Resume/Chaitanya_Mallelli_Resume.pdf' WHERE \"IsActive\" = true AND (\"FilePath\" IS NULL OR \"FilePath\" = '');"); } catch { }
        // Auto-apply: job ID column on ScoutedJobs
        try { db.Database.ExecuteSqlRaw("ALTER TABLE \"ScoutedJobs\" ADD COLUMN \"JobId\" character varying(100) NULL;"); } catch { }
        // Auto-apply: performance indexes
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_auto_apply_logs_url ON \"AutoApplyLogs\" (\"JobUrl\");"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_auto_apply_logs_applied_at ON \"AutoApplyLogs\" (\"AppliedAt\" DESC);"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS idx_auto_apply_logs_company_role ON \"AutoApplyLogs\" (\"CompanyName\", \"JobTitle\");"); } catch { }

        // Generate the creation DDL script from our EF model
        var sql = db.Database.GenerateCreateScript();

        // Split DDL by semicolon to execute each statement separately.
        // If a table, index, or constraint already exists, PostgreSQL will throw an error for that statement,
        // but we catch and ignore it, allowing the remaining missing tables to be created successfully.
        var statements = sql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawStatement in statements)
        {
            var statement = rawStatement.Trim();
            if (string.IsNullOrWhiteSpace(statement)) continue;

            try
            {
                db.Database.ExecuteSqlRaw(statement);
            }
            catch (Exception ex)
            {
                // Expected when a table/constraint/index already exists
                Console.WriteLine($"Database initialization statement skipped or already applied: {ex.Message}");
            }
        }

        // Manually seed the default profile since GenerateCreateScript doesn't output INSERT statements
        try
        {
            if (!db.UserProfiles.Any())
            {
                db.UserProfiles.Add(new UserProfile
                {
                    Id = 1,
                    FullName = "Chaitanya Mallelli",
                    Email = "MallelliChaitanya5@gmail.com",
                    Phone = "+91 9390981596",
                    LinkedInUrl = "https://www.linkedin.com/in/chaitanya-mallelli-7a9b76204/"
                });
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to seed default user profile: {ex.Message}");
        }

        // Seed default UserJobPreferences (Id=1) — auto-apply enabled, 3–4 year experience filter
        try
        {
            if (!db.UserJobPreferences.Any())
            {
                db.UserJobPreferences.Add(new UserJobPreferences
                {
                    Id                  = 1,
                    AutoApplyEnabled    = true,
                    MinExperienceYears  = 3,
                    MaxExperienceYears  = 4,
                    MinAtsScore         = 30,
                    DailyReportUtcHour  = 15,
                    UpdatedAt           = DateTime.UtcNow
                });
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to seed UserJobPreferences: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Critical database initialization error: {ex.Message}");
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// app.MapHub<JobAutomation.Hubs.InterviewHub>("/interviewHub"); // Temporarily disabled Co-Pilot

app.MapControllers(); // For API controllers (Telegram webhook)

app.Run();
