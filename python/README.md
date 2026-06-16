# JobFlow AI — Python Edition

A faithful Python port of the original ASP.NET Core **AutoMail / JobAutomation** project.
Same features, same Postgres/Supabase schema (identical table & column names), same Telegram
bot flow, same dashboard — rebuilt on **FastAPI + SQLAlchemy + Jinja2 + APScheduler + Playwright**.

## Feature parity

| Area | Original (.NET) | This port |
|------|-----------------|-----------|
| Web framework | ASP.NET Core MVC | FastAPI + Jinja2 |
| ORM / DB | EF Core + Npgsql | SQLAlchemy + psycopg2 |
| Background workers | `IHostedService` ×4 | APScheduler jobs ×4 |
| Scrapers | Microsoft.Playwright | playwright-python |
| Email | MailKit (SMTP + IMAP) | smtplib + imap-tools |
| Telegram | raw HTTP Bot API | httpx Bot API |
| AI | Gemini REST | Gemini REST (httpx) |
| Live interview | SignalR | FastAPI WebSocket (`/interviewHub`) |

Everything is ported: the Telegram pipeline (text/URL/image/PDF/voice + inline-keyboard
callbacks), duplicate detection, resume matching + ATS scoring, recruiter-email discovery,
tone-based email generation, auto-apply with preference filters, the LinkedIn/Naukri/Indeed
scrapers (incl. the SDUI fixes), Gmail reply monitoring & classification, follow-up reminders,
the daily HTML report, email open-tracking pixel, and the full dashboard UI.

## Setup

**Requires Python 3.11+** (the SQLAlchemy `Mapped[...]` model annotations use PEP-604 unions).

```bash
cd python
python -m venv .venv
.venv\Scripts\activate            # Windows  (use: source .venv/bin/activate on macOS/Linux)
pip install -r requirements.txt
python -m playwright install chromium   # for the scrapers / direct-apply
```

### Configure

Secrets are read from `appsettings.json` (+ optional `appsettings.local.json`), exactly like
the original. Environment variables override using `Section__Key` (double underscore), e.g.
`Telegram__BotToken=123:abc`. Fill in:

- `ConnectionStrings:DefaultConnection` — your Postgres/Supabase connection string (Npgsql format is parsed automatically)
- `GeminiApi:ApiKey`
- `Telegram:BotToken`
- `Gmail:AppPassword`
- `LinkedIn:Email` / `LinkedIn:Password` (for the scraper)
- `AppPasscode` — dashboard login passcode
- `AppBaseUrl` — public HTTPS base URL (for the email open-tracking pixel)

> Tip: point `ConnectionStrings:DefaultConnection` at the **same** database the .NET app uses —
> table/column names are identical, so both apps can share it.

### Run

```bash
python run.py
# → http://localhost:5128  (login with AppPasscode)
```

The four background jobs start automatically:
- **Job scout** every 6h (first run ~1 min after startup)
- **Follow-up reminders** every 1h
- **Gmail reply monitor** every 15 min
- **Daily report** at `UserJobPreferences.DailyReportUtcHour`

### Telegram webhook

Open **Telegram Bot** in the sidebar → register your public HTTPS URL (e.g. an ngrok tunnel).
`/api/telegram/webhook` is appended automatically. The startup log prints a clear
`TELEGRAM UNREACHABLE` warning if the Bot API is blocked (e.g. ISP/DNS block) instead of hanging.

## Project layout

```
python/
  app/
    main.py            FastAPI app + startup wiring (Program.cs)
    config.py          appsettings.json loader (IConfiguration)
    database.py        SQLAlchemy engine/session (AppDbContext)
    db_init.py         create tables + seed defaults
    models.py          ORM models (Models/*.cs)
    schemas.py         DTOs (DTOs/*.cs)
    auth.py            passcode middleware (PasscodeAuthFilter)
    templating.py      Jinja2 env + IST filters + scout-sidebar global
    utils.py           IST datetime + HTML escape (DateTimeExtensions)
    services/          all Services/*.cs (+ scrapers/, factory.py = DI scope)
    workers/           scheduler.py + jobs.py (Workers/*.cs)
    routers/           all Controllers/*.cs
    templates/         Razor views -> Jinja2
    static/            site.css / site.js
```
