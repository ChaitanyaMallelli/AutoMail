# AutoMail (JobFlow AI)

An AI-powered automated job application assistant and Telegram bot. This application automatically extracts job details from images, PDFs, URLs, or text, matches them against your resume, and generates tailored, professional application emails using Google Gemini AI.

## Key Features

- 📸 **Multi-Modal Input:** Extract job listings from screenshots (OCR), PDFs, text messages, or direct URLs (LinkedIn, Indeed).
- 🧠 **Smart AI Extraction & Matching:** Leverages Gemini to extract Company, Role, Skills, and computes a Match % against your profile.
- 🎨 **Multi-Tone Email Generation:** Dynamically generate emails with Professional, Enthusiastic, or Concise tones.
- 🔄 **Conversational UI (Telegram):** Receive duplicate warnings, low-match alerts (smart filtering), and tone selection buttons directly in Telegram.
- 📈 **Application Dashboard:** Manage your resume, user profile, and track all sent applications in a web dashboard.
- 📊 **Response Tracking:** Log interviews, offers, and rejections. Tracks your overall application response rate.
- ⏰ **Smart Follow-Ups:** A background service automatically prompts you on Telegram to follow up on applications sent 3+ days ago without a response.

## Prerequisites

1. **.NET 8.0 SDK**
2. **PostgreSQL** database (Local or Supabase/Neon)
3. **Google Gemini API Key** (Free tier available)
4. **Telegram Bot Token** (Create one via BotFather)
5. **Ngrok** (For local testing of the Telegram Webhook)
6. **Gmail App Password** (For sending emails via SMTP)

## Setup & Configuration

1. **Clone the repository.**
2. **Configure Environment Variables:**
   Update `appsettings.json` or use User Secrets / Environment Variables to set:
   - `ConnectionStrings:DefaultConnection`: Your PostgreSQL connection string.
   - `GeminiApi:ApiKey`: Your Gemini API Key.
   - `Telegram:BotToken`: Your Telegram Bot Token.
   - `Telegram:WebhookUrl`: Your Ngrok HTTPS URL (e.g., `https://your-ngrok.ngrok.dev/api/telegram/webhook`).
   - `Gmail:AppPassword`: Your 16-character Gmail App Password.
   - `AppPasscode`: The passcode to access the web dashboard (default is your phone number).

3. **Run the Database Migrations:**
   The application will automatically attempt to create the tables on the first run.
   Alternatively, you can run Entity Framework Core migrations if preferred.

4. **Run the Application:**
   ```bash
   dotnet run
   ```

5. **Expose your local server via Ngrok:**
   ```bash
   ngrok http https://localhost:7198
   ```
   *Make sure the `WebhookUrl` in configuration matches the URL Ngrok gives you.*

## Usage

1. Open the Web Dashboard (default: `https://localhost:7198`).
2. Log in using the `AppPasscode`.
3. Go to **Resumes & Profile** to fill out your details and upload a PDF of your resume (you can use the Auto-Parse feature to extract details).
4. Go to Telegram and send your Bot a job posting URL, screenshot, or text.
5. Review the extracted details, select a tone, and click **Approve & Send**.

## Background Services

- `FollowUpBackgroundService`: Runs every hour to check for applications that have been in the "Sent" status for more than 3 days. It sends an interactive Telegram message asking if you want to auto-generate a follow-up email.
