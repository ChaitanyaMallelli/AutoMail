# AutoMail — JobFlow AI · Complete Workflow Guide

> **ASP.NET Core 8 MVC** · **PostgreSQL (Supabase)** · **Gemini AI** · **Telegram Bot** · **Gmail SMTP/IMAP** · **Playwright**

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Architecture Map](#2-architecture-map)
3. [How Job Posts Enter the System](#3-how-job-posts-enter-the-system)
4. [The Core Processing Pipeline](#4-the-core-processing-pipeline)
5. [AI Engine — Gemini Service](#5-ai-engine--gemini-service)
6. [Resume Matching](#6-resume-matching)
7. [Email Generation & Sending](#7-email-generation--sending)
8. [Email Open Tracking (Pixel)](#8-email-open-tracking-pixel)
9. [Gmail Reply Detection (IMAP)](#9-gmail-reply-detection-imap)
10. [LinkedIn Scout — Auto Job Discovery](#10-linkedin-scout--auto-job-discovery)
11. [Direct Apply (Naukri / Indeed)](#11-direct-apply-naukri--indeed)
12. [Background Services](#12-background-services)
13. [Telegram Bot Integration](#13-telegram-bot-integration)
14. [Dashboard & Web UI](#14-dashboard--web-ui)
15. [Data Models](#15-data-models)
16. [Tech Stack Summary](#16-tech-stack-summary)
17. [End-to-End Flow Diagrams](#17-end-to-end-flow-diagrams)

---

## 1. System Overview

AutoMail is an **AI-powered job application automation platform**. It watches for job posts (manually or automatically), uses Google Gemini to extract details, matches them against your resume, generates a tailored email, and either sends it automatically or asks for your approval via Telegram or the web dashboard.

**Three core loops:**

| Loop | Trigger | Frequency |
|---|---|---|
| **Apply Loop** | You paste/upload a job post | On-demand |
| **Scout Loop** | Cron background service | Every hour |
| **Reply Monitor** | Background IMAP polling | Every 15 min |

---

## 2. Architecture Map

```
┌──────────────────────────────────────────────────────────────────┐
│                        ENTRY POINTS                              │
│                                                                  │
│  Telegram Bot       Web Dashboard        LinkedIn Scout          │
│  (text/image/       (URL / Upload        (Playwright scraper     │
│   PDF / URL)         page)               + Gemini filter)        │
└────────────┬────────────────┬───────────────────┬───────────────┘
             │                │                   │
             ▼                ▼                   ▼
┌─────────────────────────────────────────────────────────────────┐
│               JOB PROCESSING SERVICE (Core Pipeline)            │
│                                                                  │
│  1. Receive input  →  2. Gemini extracts details                │
│  3. Duplicate check  →  4. Resume matching                      │
│  5. Email finder   →  6. Save JobPost to DB                     │
│  7. Gemini generates email  →  8. Save GeneratedEmail to DB     │
└───────────────────────────────┬─────────────────────────────────┘
                                │
             ┌──────────────────┴──────────────────┐
             ▼                                     ▼
   WEB trigger (chatId=999)          TELEGRAM trigger (chatId=real)
   → Show on Dashboard               → Send approval request to phone
   → You click "Send"                → You tap Approve
             │                                     │
             └──────────────────┬──────────────────┘
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                       EMAIL SERVICE                             │
│  Gmail SMTP  +  Resume attachment  +  Tracking pixel embed      │
└───────────────────────────────┬─────────────────────────────────┘
                                │
             ┌──────────────────┴──────────────────┐
             ▼                                     ▼
   Email Open Tracking                  Gmail Reply Detection
   (1×1 pixel → /track/open/{guid})    (IMAP polling every 15 min)
   → Records OpenedAt                  → Gemini classifies reply
   → Telegram notification             → Updates status + Telegram alert
```

---

## 3. How Job Posts Enter the System

There are **4 ways** a job post can enter the pipeline:

### 3a. Telegram Bot (Manual)

You send a message to your Telegram bot. The bot accepts:

| What you send | How it's processed |
|---|---|
| Plain text (job description pasted) | `ProcessTextAsync` → Gemini text extraction |
| Screenshot / image of job post | `ProcessImageAsync` → Gemini vision (reads image) |
| PDF file | `ProcessPdfAsync` → Gemini PDF parsing |
| URL link to a job post | `ProcessUrlAsync` → Fetches HTML → Gemini extraction |

The bot is registered via **webhook** (`/api/telegram/webhook`). Every Telegram message hits `TelegramController`, which identifies the type and routes to the correct `JobProcessingService` method.

### 3b. Web Dashboard — Upload Page (`/Upload`)

You paste a URL or upload a file directly in the browser. Same `JobProcessingService` methods are called with `chatId = 999` (the web trigger convention) so progress is shown on the dashboard instead of sent to Telegram.

### 3c. LinkedIn Scout — Automated Discovery

A background cron service (`JobScoutBackgroundService`) runs every hour. It:
1. Launches a headless Playwright browser
2. Searches LinkedIn for keywords like `"dotnet developer"`, `".net developer banglore"`
3. Scrapes the raw post text + URL
4. Sends the batch to Gemini for relevance filtering (one API call for all posts)
5. Saves matched jobs as `ScoutedJob` records
6. Sends Telegram alerts for each match

When you click **Apply** on a scouted job from the dashboard sidebar, it calls `DashboardController.ApplyScoutedJob()` which triggers the full email pipeline in the background.

### 3d. Dashboard Sidebar — Apply Scouted Job

Clicking "Apply" on a scouted job in the ScoutSidebar:
1. Sets `ScoutedJob.Status = Applied`
2. Spawns a background `Task.Run` with its own DI scope
3. Runs the email pipeline via `JobProcessingService.ProcessUrlAsync`
4. Progress is tracked in `TelegramProgressTracker` under `chatId=999`
5. You are redirected to `/Telegram/Progress` to watch live

---

## 4. The Core Processing Pipeline

Every job post — regardless of entry point — goes through `JobProcessingService`. Here are the numbered steps:

```
Step 1    Receive input (text / image / PDF / URL)
          └─ If URL: fetch HTML with HttpClient (15s timeout)

Step 2    Gemini AI extracts:
          • CompanyName, Role, RequiredSkills
          • RecruiterEmail (if present in post)
          • ExperienceRequired, Location
          └─ Cached by SHA-256 hash of input (2h TTL) — avoids repeat Gemini calls

Step 2.5  Duplicate detection
          └─ Checks DB for same Company + Role (non-Skipped status)
          └─ If duplicate found → warns via progress tracker, continues anyway

Step 3    Resume matching
          └─ ResumeMatchingService compares job skills vs your active resume
          └─ Outputs: AtsScore (0-100), SkillMatchPercentage (0-100), MissingSkills[]

Step 3.5  Smart filter warning
          └─ If SkillMatchPercentage < 30% → warns about low match in progress feed

Step 3.8  Email finder (only if recruiter email not found in job post)
          └─ CompanyEmailFinderService scrapes company website for hr@/careers@ emails
          └─ If not found → asks Gemini "what is the likely HR email for {company}?"

Step 4    Save JobPost to PostgreSQL (Status = Pending or EmailGenerated)

Step 5    Gemini generates tailored email draft:
          • Subject line
          • Email body (tone: professional / friendly / formal / concise)
          • Highlights matching skills from your resume
          └─ Saves as GeneratedEmail linked to JobPost

Step 6    Routing:
          • chatId = 999 (web) → "Review on dashboard" message shown
          • chatId = real Telegram ID → approval request sent to your phone
```

---

## 5. AI Engine — Gemini Service

`GeminiService` wraps the Google Gemini API (`gemini-2.0-flash-lite` model).

### What Gemini does in this project

| Task | Method | Input | Output |
|---|---|---|---|
| Extract from text | `ExtractJobDetailsFromTextAsync` | Job post text | `JobExtractionResult` (JSON) |
| Extract from image | `ExtractJobDetailsFromImageAsync` | Image bytes | `JobExtractionResult` |
| Extract from PDF | `ExtractJobDetailsFromPdfAsync` | PDF bytes | `JobExtractionResult` |
| Extract from URL | `ExtractJobDetailsFromUrlContentAsync` | HTML content | `JobExtractionResult` |
| Parse resume | `ExtractResumeDetailsFromPdfAsync` | Resume PDF | `ResumeExtractionResult` |
| Generate email | `GenerateEmailAsync` | Job + Resume + Profile | `EmailDraftDto` |
| Batch relevance check | `BatchIsPostRelevantAsync` | List of post texts | `List<bool>` |
| Classify reply | `ClassifyReplyAsync` | Reply email text | `"interview"` / `"rejected"` / `"interested"` / `"other"` |
| Find recruiter email | (inside CompanyEmailFinderService) | Company name | Likely HR email |

### Performance: Caching

Every Gemini call that takes text/URL input is cached in `IMemoryCache`:
- Cache key = SHA-256 hash of input string (first 16 hex chars)
- TTL = 2 hours
- This means if you submit the same job post twice within 2 hours, Gemini is only called once

### Performance: Batch Relevance

Instead of calling Gemini once per scraped LinkedIn post, `BatchIsPostRelevantAsync` sends **all posts in one prompt** and gets back a JSON array of `true/false` values — saves N-1 API calls per scout cycle.

---

## 6. Resume Matching

`ResumeMatchingService` runs entirely in-memory (no AI call needed).

**How it works:**
1. Reads your active resume's skills from the database
2. Reads the job's `RequiredSkills` string
3. Tokenizes both into keyword lists
4. Calculates:
   - `SkillMatchPercentage` = matched skills / total required skills × 100
   - `AtsScore` = weighted formula including match %, experience gap, location match
   - `MissingSkills` = skills in job but not in your resume

These scores are stored on `JobPost` and shown as columns on the Dashboard.

---

## 7. Email Generation & Sending

### Generation
Gemini generates a job application email that:
- Addresses the recruiter by title (if known)
- Highlights only the matching skills (from resume match result)
- Uses the selected **tone** (professional / friendly / formal / concise)
- Follows a professional email format with proper sign-off using your `UserProfile.FullName`

### Sending Flow (`/Email/Preview` page)

```
1. You open Email Preview  →  see the generated subject + body
2. Edit if needed (inline editor)
3. Choose tone  →  regenerates with Gemini
4. Click "Send Email"
5. EmailController.SendEmail() is called:
   a. Loads active Resume → resolves file path → attaches PDF
   b. If AppBaseUrl configured → generates TrackingToken (GUID) → embeds 1×1 pixel
   c. EmailService.SendEmailAsync() → Gmail SMTP (port 587, TLS)
   d. JobPost.Status → "Sent"
   e. If Telegram chatId exists → sends "Email sent!" confirmation to your phone
```

### Resume Attachment
- Checks `Resume.FilePath` in DB
- Constructs full OS path: `Path.Combine(currentDir, filePath.TrimStart('/'))`
- Verifies file exists with `System.IO.File.Exists()` before attaching

---

## 8. Email Open Tracking (Pixel)

When you send an email with `AppBaseUrl` configured (your ngrok URL in `appsettings.json`):

1. `EmailService` generates a `TrackingToken` (GUID) and saves it to `GeneratedEmail.TrackingToken`
2. The email HTML body gets a hidden `<img>` tag:
   ```html
   <img src="https://your-ngrok-url/track/open/{guid}" width="1" height="1" />
   ```
3. When the recruiter **opens the email**, their email client loads this image
4. `TrackingController.TrackOpen(guid)` is hit:
   - Sets `GeneratedEmail.OpenedAt = DateTime.UtcNow`
   - Sets `JobPost.Status = Opened`
   - Sends you a Telegram message: "📬 Your email to {Company} was opened!"
5. On Email History / Preview pages you see an **"Opened"** badge with timestamp

> **Note:** Requires your app to be publicly accessible (ngrok or deployed server). Email clients that block remote images won't trigger the pixel.

---

## 9. Gmail Reply Detection (IMAP)

`GmailReplyMonitorService` runs as a hosted background service.

**Cycle (every 15 minutes):**
```
1. Connect to Gmail IMAP (imap.gmail.com:993, SSL)
2. Search Inbox for emails received since last check
3. For each incoming email:
   a. Check if Subject contains "Re:" + a subject from a sent GeneratedEmail
   b. If match found:
      → Record GeneratedEmail.RepliedAt = now
      → Store ReplySubject, ReplySnippet (first 500 chars)
      → Call Gemini.ClassifyReplyAsync() → "interview" / "rejected" / "interested" / "other"
      → Store ReplyClassification
      → Update JobPost.Status (InterviewScheduled / Rejected / etc.)
      → Send Telegram alert: "🎉 Reply from {Company}: {classification}"
4. Disconnect IMAP
```

On the Email History page you see:
- **"Replied"** badge in teal
- Reply classification badge (Interview / Rejected / Interested)
- Reply snippet preview card

---

## 10. LinkedIn Scout — Auto Job Discovery

`JobScoutBackgroundService` runs hourly. It calls `JobScoutManager.RunScoutCycleAsync()`.

### Full Scout Cycle

```
1. Read TelegramChatId from UserProfile (survives data clears)
   └─ Fallback: read from most recent JobPost

2. For each registered IJobBoardScraper (currently: LinkedInScraperService only):
   a. Launch Playwright Chromium (headless)
   b. Log into LinkedIn using credentials from appsettings.local.json
   c. For each search keyword:
      • Search LinkedIn Posts
      • Scrape: post text, post URL, author, timestamp
   d. Return list of ScoutedJob objects with Board="LinkedIn"

3. Deduplicate within this batch (group by URL, take first)

4. Bulk dedup against DB (single SQL query — all URLs at once)
   └─ Skips any URL or RawText already seen

5. Batch Gemini relevance check (ONE API call for all fresh posts):
   └─ Filter: must be 3+ years experience .NET role
   └─ Filter: must have contact info (email or phone) OR be from LinkedIn

6. For relevant posts: Status = SentToTelegram → saved to DB
   For irrelevant posts: Status = IgnoredByGemini → saved to DB

7. Single bulk SaveChangesAsync (one DB round-trip for all)

8. Send Telegram alerts for each matched job:
   └─ Shows company, role, preview of post, "Apply" button
```

### Scout Sidebar

The `ScoutSidebarViewComponent` loads the last 8 scouted jobs and renders them in the sidebar on every page. The "Apply" button triggers `DashboardController.ApplyScoutedJob`.

---

## 11. Direct Apply (Naukri / Indeed)

> Currently **in codebase but disabled** in DI registration. LinkedIn email pipeline is active instead.

`DirectApplyService` uses Playwright to:
1. Navigate to the Naukri / Indeed job URL
2. Detect "Easy Apply" or "Apply Now" button
3. Fill in the application form using `UserProfile` data
4. Upload resume if a file upload field is found
5. Submit the form

For Naukri: uses session cookies saved to `Data/Sessions/naukri_session.json` — you log in once in a visible browser window, then subsequent runs reuse the session.

**Dual-channel apply logic** (when re-enabled):
- For Naukri/Indeed jobs with contact info in post: **Direct Apply** → **Email pipeline** (both)
- For Naukri/Indeed jobs with no contact info: **Direct Apply** only
- For LinkedIn jobs: **Email pipeline** only (always)

---

## 12. Background Services

Three long-running `BackgroundService` hosted workers run alongside the web app:

| Service | File | What it does | Interval |
|---|---|---|---|
| `JobScoutBackgroundService` | Workers/ | Runs LinkedIn scrape cycle | Every 1 hour |
| `FollowUpBackgroundService` | Workers/ | Sends follow-up reminders for jobs in "Sent" status with no reply | Daily |
| `GmailReplyMonitorService` | Workers/ | Polls Gmail IMAP for recruiter replies | Every 15 min |

All background services use `IServiceScopeFactory.CreateScope()` to safely access scoped services (like `AppDbContext`) without "disposed object" errors.

---

## 13. Telegram Bot Integration

Your Telegram bot is the **primary mobile interface** for the platform.

### Webhook Flow
```
You send message → Telegram servers → POST /api/telegram/webhook
→ TelegramController.Webhook()
→ Routes by message type:
   • Text message  → check if URL → ProcessUrlAsync or ProcessTextAsync
   • Photo         → ProcessImageAsync
   • Document PDF  → ProcessPdfAsync
   • Callback query (button taps) → handles Approve / Skip / Tone change
```

### What you receive on Telegram

| Event | Telegram message |
|---|---|
| New scout job found | Job post preview + "Apply" button |
| Email draft ready | Subject + body preview + Approve / Edit / Skip buttons |
| Email sent | Confirmation with company name |
| Email opened | "📬 Your email to {Company} was opened!" |
| Reply received | "🎉 Reply from {Company}: interview/rejected/interested" |
| Follow-up sent | Confirmation |

### chatId = 999 Convention
When you trigger a job from the **web dashboard** (not Telegram), the system uses `chatId = 999` internally. This means:
- Progress goes to `/Telegram/Progress` page (web UI) instead of your phone
- Final step says "Review on dashboard" instead of sending to Telegram
- Telegram alerts are skipped

---

## 14. Dashboard & Web UI

The web app runs at `http://localhost:PORT` (or your ngrok URL).

### Pages

| URL | Page | Purpose |
|---|---|---|
| `/` or `/Dashboard` | **Dashboard** | Job table with filters, stats cards, scout sidebar |
| `/Upload` | **Upload** | Paste URL or upload image/PDF to start pipeline |
| `/Email/Preview/{id}` | **Email Preview** | Edit and send generated email |
| `/Email/History` | **Email History** | All sent emails, open status, reply status |
| `/Resume` | **Resume** | Upload/manage your resume, AI parsing |
| `/Telegram` | **Telegram Setup** | Bot config, webhook URL |
| `/Telegram/Progress` | **Progress** | Live pipeline status (web-triggered jobs) |
| `/Login` | **Login** | App passcode entry |

### Dashboard Stats Cards

| Card | Formula |
|---|---|
| Total Jobs | All non-Skipped jobs |
| Pending | Pending + EmailGenerated |
| Sent | Sent + FollowUpSent + Interviews + Rejected + Offered + Ghosted |
| Response Rate | (Interviews + Offers + Rejected) / Sent × 100% |
| Avg Match | Average SkillMatchPercentage across all jobs |

Stats are computed in **one grouped SQL query** — no N+1 queries.

### Scout Sidebar

- Shows last 8 scouted LinkedIn jobs
- Board badge (LinkedIn / Naukri / Indeed)
- "Apply" button triggers the full email pipeline in background
- "Run Scout Now" button manually triggers a scout cycle

---

## 15. Data Models

### Core Tables

```
UserProfile
├── FullName, Email, Phone, LinkedInUrl
└── TelegramChatId  ← persists chat ID even after job data clears

Resume
├── FileName, FilePath (relative: "Resume/Chaitanya_Mallelli_Resume.pdf")
├── ParsedSkills, ParsedExperience
└── IsActive  ← only one active at a time

JobPost
├── CompanyName, Role, Location
├── RequiredSkills, ExperienceRequired
├── RecruiterEmail, RawContent, ImagePath
├── Source (Telegram / Upload / Scout)
├── SourceType (Text / Image / PDF / URL)
├── AtsScore, SkillMatchPercentage
├── Status (Pending → EmailGenerated → Sent → Opened → Replied / Rejected / ...)
├── TelegramChatId, FollowUpReminderSent
└── CreatedAt, UpdatedAt

GeneratedEmail
├── JobPostId (FK → JobPost)
├── Subject, Body, RecipientEmail, Tone
├── TrackingToken (GUID) ← for open pixel
├── OpenedAt             ← set by TrackingController
├── MessageId            ← Gmail Message-ID header
├── RepliedAt, ReplySubject, ReplySnippet, ReplyClassification
└── CreatedAt

ScoutedJob
├── LinkedInUrl, RawText, KeywordMatched
├── Board (LinkedIn / Naukri / Indeed)
├── Status (Pending → SentToTelegram / IgnoredByGemini / Applied)
└── CreatedAt
```

### Status Lifecycle

```
JobPost.Status:
  Pending
    └─ EmailGenerated  (Gemini draft ready)
         └─ Sent  (email delivered)
              ├─ Opened  (tracking pixel fired)
              ├─ FollowUpSent
              ├─ InterviewScheduled  (Gemini classified reply as "interview")
              ├─ Rejected            (Gemini classified reply as "rejected")
              ├─ Offered
              └─ Ghosted
  Skipped  (user manually skipped)
  Failed   (pipeline error)
```

---

## 16. Tech Stack Summary

| Layer | Technology |
|---|---|
| **Framework** | ASP.NET Core 8 MVC (Razor Views) |
| **Database** | PostgreSQL via Supabase (EF Core 8, no migrations — DDL at startup) |
| **AI** | Google Gemini API (`gemini-2.0-flash-lite`) |
| **Email Send** | Gmail SMTP via `System.Net.Mail` (App Password auth) |
| **Email Receive** | Gmail IMAP via MailKit |
| **Telegram** | Telegram Bot API (webhook, inline keyboard callbacks) |
| **Browser Automation** | Microsoft Playwright (LinkedIn scraping, Direct Apply) |
| **Caching** | `IMemoryCache` (2h TTL on Gemini responses) |
| **Background Jobs** | `IHostedService` / `BackgroundService` (3 workers) |
| **DI** | `IServiceScopeFactory` for background tasks (avoids disposed context) |
| **Frontend** | Bootstrap 5, Bootstrap Icons, Inter font, custom CSS (dark theme) |
| **Auth** | Passcode filter (`PasscodeAuthFilter`) on all routes except `/track/` |
| **Progress Tracking** | `TelegramProgressTracker` (static `ConcurrentDictionary<chatId, List<string>>`) |

---

## 17. End-to-End Flow Diagrams

### Flow A — You paste a job URL on Telegram

```
You → Telegram: "https://linkedin.com/jobs/view/12345"
       ↓
TelegramController.Webhook()
       ↓
JobProcessingService.ProcessUrlAsync(url, chatId=yourChatId)
       ↓
  HttpClient fetches page HTML
       ↓
  GeminiService.ExtractJobDetailsFromUrlContentAsync()
  (cached by URL hash, 2h TTL)
       ↓
  FindDuplicateAsync() — skip if already applied
       ↓
  ResumeMatchingService.Match() — ATS score, skill %
       ↓
  CompanyEmailFinderService — if no recruiter email in post
       ↓
  Save JobPost (Status=Pending)
       ↓
  GeminiService.GenerateEmailAsync() — tailored draft
       ↓
  Save GeneratedEmail (Status=EmailGenerated)
       ↓
TelegramService.SendApprovalRequestAsync()
→ You receive: Subject + Body + [Approve] [Edit Tone] [Skip] buttons
       ↓
You tap [Approve]
       ↓
TelegramController.HandleCallback("approve_{jobId}")
       ↓
EmailService.SendEmailAsync()
  + Attach Resume PDF
  + Embed tracking pixel
  + Send via Gmail SMTP
       ↓
JobPost.Status = Sent
TelegramService: "✅ Email sent to {Company}!"
```

---

### Flow B — LinkedIn Scout finds a match

```
JobScoutBackgroundService (every 1 hour)
       ↓
JobScoutManager.RunScoutCycleAsync()
       ↓
LinkedInScraperService.ScrapePostsAsync(keywords)
  → Playwright: login LinkedIn → search → scrape N posts
       ↓
Deduplicate (URL + RawText) against DB in 1 SQL query
       ↓
GeminiService.BatchIsPostRelevantAsync(allTexts)
  → One Gemini call → List<bool>
       ↓
Relevant posts → Status=SentToTelegram
Irrelevant posts → Status=IgnoredByGemini
Bulk DB insert (1 SaveChangesAsync)
       ↓
TelegramService.SendJobAlertAsync(chatId, job)
→ You receive post preview + [Apply] button on Telegram
       ↓
You tap [Apply] (or click Apply on Dashboard sidebar)
       ↓
DashboardController.ApplyScoutedJob(id)
  → Background Task.Run (new DI scope)
  → JobProcessingService.ProcessUrlAsync(url, chatId=999)
  → Full pipeline runs (Steps 1–6 above)
  → Progress shown at /Telegram/Progress
```

---

### Flow C — Email Open Tracking

```
EmailService.SendEmailAsync() with trackingBaseUrl
  → Generates GUID TrackingToken
  → Saves to GeneratedEmail.TrackingToken
  → Embeds <img src="/track/open/{guid}"> in email HTML
       ↓
Recruiter opens email in Gmail / Outlook
  → Email client loads the 1×1 pixel image
       ↓
GET /track/open/{guid}
TrackingController.TrackOpen(guid)
  → GeneratedEmail.OpenedAt = now
  → JobPost.Status = Opened
  → TelegramService: "📬 Your email to {Company} was opened!"
  → Returns 1×1 transparent GIF (no visible effect for recruiter)
```

---

*Last updated: June 2026*
