"""The four background jobs (ports of the BackgroundService workers).

- job_scout_cycle        — every 6 hours (JobScoutBackgroundService)
- follow_up_check        — every 1 hour (FollowUpBackgroundService)
- gmail_reply_check      — every 15 minutes (GmailReplyMonitorService)
- send_daily_report      — once per day at DailyReportUtcHour (DailyReportBackgroundService)

Each opens its own DB session (a fresh "scope"), mirroring the C# CreateScope() pattern.
"""
from __future__ import annotations

import html as html_lib
import logging

from imap_tools import AND, MailBox
from sqlalchemy import and_

from ..config import config
from ..database import SessionLocal
from ..models import AutoApplyStatus, GeneratedEmail, JobPost, JobStatus, UserJobPreferences, UserProfile
from ..services.factory import build_services
from ..utils import to_ist, utcnow
from datetime import timedelta

logger = logging.getLogger(__name__)


# ── Job Scout (every 6h) ──────────────────────────────────────────────────────
async def job_scout_cycle() -> None:
    with SessionLocal() as db:
        try:
            services = build_services(db)
            await services.scout_manager().run_scout_cycle()
        except Exception as ex:  # noqa: BLE001
            logger.error("Error occurred during job scouting cycle: %s", ex)


# ── Follow-up reminders (every 1h) ────────────────────────────────────────────
async def follow_up_check() -> None:
    with SessionLocal() as db:
        try:
            services = build_services(db)
            telegram = services.telegram
            cutoff = utcnow() - timedelta(days=3)
            jobs = (
                db.query(JobPost)
                .join(GeneratedEmail, GeneratedEmail.JobPostId == JobPost.Id)
                .filter(JobPost.Status == JobStatus.Sent, GeneratedEmail.SentAt <= cutoff)
                .all()
            )
            for job in jobs:
                if job.TelegramChatId:
                    logger.info("Prompting follow-up for Job %s to Chat %s", job.Id, job.TelegramChatId)
                    keyboard = {
                        "inline_keyboard": [
                            [{"text": "📨 Auto-Generate & Send Follow-up", "callback_data": f"followup_send:{job.Id}"}],
                            [{"text": "✅ I got a response!", "callback_data": f"followup_gotresponse:{job.Id}"}],
                            [{"text": "❌ Dismiss", "callback_data": f"followup_dismiss:{job.Id}"}],
                        ]
                    }
                    message = (
                        f"⏰ <b>Follow-Up Reminder!</b>\n\nIt's been 3 days since you applied to "
                        f"<b>{job.Role}</b> at <b>{job.CompanyName}</b>.\n\n"
                        f"Have you heard back? I can write a polite follow-up email for you."
                    )
                    await telegram.send_keyboard_reply(job.TelegramChatId, message, keyboard)
        except Exception as ex:  # noqa: BLE001
            logger.error("Error occurred executing follow-up check: %s", ex)


# ── Gmail reply monitor (every 15 min) ────────────────────────────────────────
async def gmail_reply_check() -> None:
    imap_server = config.get("Gmail:ImapServer") or "imap.gmail.com"
    sender_email = config.get("Gmail:SenderEmail")
    app_password = config.get("Gmail:AppPassword")
    if not sender_email or not app_password:
        logger.warning("Gmail credentials not configured. Skipping reply check.")
        return

    with SessionLocal() as db:
        try:
            services = build_services(db)
            gemini = services.gemini
            telegram = services.telegram

            sent_emails = (
                db.query(GeneratedEmail)
                .filter(
                    GeneratedEmail.IsSent.is_(True),
                    GeneratedEmail.RepliedAt.is_(None),
                    GeneratedEmail.RecipientEmail.isnot(None),
                )
                .all()
            )
            if not sent_emails:
                return

            recruiter_emails = {e.RecipientEmail.lower() for e in sent_emails}
            logger.info("Checking Gmail INBOX for replies from %d recruiters.", len(recruiter_emails))

            since = (utcnow() - timedelta(days=60)).date()
            with MailBox(imap_server).login(sender_email, app_password, "INBOX") as mailbox:
                for msg in mailbox.fetch(AND(date_gte=since), mark_seen=False):
                    from_addr = (msg.from_ or "").lower()
                    if not from_addr or from_addr not in recruiter_emails:
                        continue
                    matched = next(
                        (e for e in sent_emails if e.RecipientEmail.lower() == from_addr and e.RepliedAt is None),
                        None,
                    )
                    if matched is None:
                        continue
                    msg_dt = msg.date.replace(tzinfo=None) if msg.date else None
                    if matched.SentAt and msg_dt and msg_dt < matched.SentAt:
                        continue

                    logger.info("Reply detected from %s for job %s", from_addr, matched.JobPostId)
                    reply_text = (msg.text or msg.html or "")[:3000]
                    company = matched.JobPost.CompanyName if matched.JobPost else "Unknown"
                    role = matched.JobPost.Role if matched.JobPost else "Unknown"

                    classification = "other"
                    try:
                        classification = await gemini.classify_reply(reply_text, company, role)
                    except Exception as ex:  # noqa: BLE001
                        logger.warning("Gemini classification failed, defaulting to 'other': %s", ex)

                    matched.RepliedAt = utcnow()
                    matched.ReplySubject = (msg.subject or "")[:200]
                    matched.ReplySnippet = reply_text[:1000]
                    matched.ReplyClassification = classification
                    if matched.JobPost is not None:
                        if classification == "interview":
                            matched.JobPost.Status = JobStatus.InterviewScheduled
                        elif classification == "rejected":
                            matched.JobPost.Status = JobStatus.Rejected
                        matched.JobPost.UpdatedAt = utcnow()
                    db.commit()

                    chat_id = matched.JobPost.TelegramChatId if matched.JobPost else None
                    if chat_id and chat_id != 999:
                        emoji = {"interview": "🎉", "rejected": "😔", "interested": "👀"}.get(classification, "📩")
                        status_text = {
                            "interview": "Interview request received!",
                            "rejected": "Application rejected.",
                            "interested": "Recruiter expressed interest.",
                        }.get(classification, "General reply received.")
                        tg_msg = (
                            f"{emoji} <b>Reply Detected!</b>\n\n<b>{company}</b> replied to your <b>{role}</b> "
                            f"application.\n<i>{status_text}</i>\n\nSnippet: {reply_text[:200]}..."
                        )
                        try:
                            await telegram.send_reply(chat_id, tg_msg)
                        except Exception:  # noqa: BLE001
                            pass
        except Exception as ex:  # noqa: BLE001
            logger.error("IMAP connection or search failed: %s", ex)


# ── Daily report (scheduled hour) ─────────────────────────────────────────────
async def send_daily_report() -> None:
    with SessionLocal() as db:
        try:
            services = build_services(db)
            email_service = services.email_service
            telegram = services.telegram

            ist_now = to_ist(utcnow())
            today_ist_start = ist_now.replace(hour=0, minute=0, second=0, microsecond=0, tzinfo=None)
            today_utc_start = today_ist_start - timedelta(hours=5, minutes=30)
            today_utc_end = today_utc_start + timedelta(days=1)

            from ..models import AutoApplyLog

            logs = (
                db.query(AutoApplyLog)
                .filter(AutoApplyLog.AppliedAt >= today_utc_start, AutoApplyLog.AppliedAt < today_utc_end)
                .order_by(AutoApplyLog.AppliedAt.desc())
                .all()
            )
            applied = [l for l in logs if l.Status == AutoApplyStatus.Applied]
            skipped = [l for l in logs if l.Status == AutoApplyStatus.Skipped]
            failed = [l for l in logs if l.Status == AutoApplyStatus.Failed]
            logger.info("Daily report: %d applied, %d skipped, %d failed", len(applied), len(skipped), len(failed))

            profile = db.query(UserProfile).first()
            prefs = db.query(UserJobPreferences).first()
            to_email = (prefs.NotificationEmail if prefs else None) or (profile.Email if profile else None)
            if not to_email or profile is None:
                logger.warning("Daily report: no notification email configured.")
                return

            report_date = today_ist_start.strftime("%d %b %Y")
            subject = f"📋 Daily Job Report — {report_date} ({len(applied)} applied, {len(skipped)} skipped)"
            body = _build_html_report(logs, applied, skipped, failed, report_date)

            temp_email = GeneratedEmail(Subject=subject, Body=body, RecipientEmail=to_email, CreatedAt=utcnow())
            # Daily report body is already HTML; send as-is (no tracking pixel).
            sent, err = email_service.send_email(temp_email, profile)
            if sent:
                logger.info("Daily report sent to %s", to_email)
            else:
                logger.warning("Daily report email failed: %s", err)

            chat_id = (profile.TelegramChatId or 0) if profile else 0
            if chat_id != 0:
                tg = (
                    f"📋 *Daily Report — {report_date}*\n\n"
                    f"✅ Applied: {len(applied)}\n"
                    f"⏭️ Skipped: {len(skipped)}\n"
                    f"❌ Failed:  {len(failed)}\n\n"
                    f"Full report sent to {to_email}"
                )
                try:
                    await telegram.send_reply(chat_id, tg)
                except Exception as ex:  # noqa: BLE001
                    logger.warning("Daily report Telegram failed: %s", ex)
        except Exception as ex:  # noqa: BLE001
            logger.error("DailyReport error: %s", ex)


def _esc(s: str | None) -> str:
    return html_lib.escape(s or "")


def _build_html_report(all_logs, applied, skipped, failed, report_date: str) -> str:
    parts: list[str] = []
    parts.append(
        f"""<html><body style="font-family:sans-serif;color:#1a1a1a;max-width:800px;margin:auto;padding:20px;">
<h2 style="color:#7c3aed;">📋 Daily Job Application Report — {report_date}</h2>
<p style="font-size:18px;">
  <span style="color:#22c55e;">✅ Applied: <b>{len(applied)}</b></span> &nbsp;|&nbsp;
  <span style="color:#f59e0b;">⏭️ Skipped: <b>{len(skipped)}</b></span> &nbsp;|&nbsp;
  <span style="color:#ef4444;">❌ Failed: <b>{len(failed)}</b></span>
</p>"""
    )

    if applied:
        parts.append(
            """<h3 style="color:#22c55e;border-bottom:2px solid #22c55e;padding-bottom:6px;">✅ Applied Jobs</h3>
<table style="width:100%;border-collapse:collapse;font-size:14px;">
<tr style="background:#f0fdf4;">
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Work Mode</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Location</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Applied At (IST)</th>
</tr>"""
        )
        for log in applied:
            time_ist = to_ist(log.AppliedAt).strftime("%H:%M")
            job_link = f'<a href="{log.JobUrl}" style="color:#7c3aed;">🔗 View</a>' if log.JobUrl else "—"
            parts.append(
                f"""<tr>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.CompanyName)}</td>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.JobTitle)} {job_link}</td>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.WorkMode or "—")}</td>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.Location or "—")}</td>
  <td style="padding:8px;border:1px solid #ddd;">{time_ist}</td>
</tr>"""
            )
        parts.append("</table><br/>")

    if skipped:
        parts.append(
            """<h3 style="color:#f59e0b;border-bottom:2px solid #f59e0b;padding-bottom:6px;">⏭️ Skipped Jobs</h3>
<table style="width:100%;border-collapse:collapse;font-size:14px;">
<tr style="background:#fffbeb;">
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Reason</th>
</tr>"""
        )
        for log in skipped:
            parts.append(
                f"""<tr>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.CompanyName)}</td>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.JobTitle)}</td>
  <td style="padding:8px;border:1px solid #ddd;color:#78716c;">{_esc(log.SkipReason or "—")}</td>
</tr>"""
            )
        parts.append("</table><br/>")

    if failed:
        parts.append(
            """<h3 style="color:#ef4444;border-bottom:2px solid #ef4444;padding-bottom:6px;">❌ Failed</h3>
<table style="width:100%;border-collapse:collapse;font-size:14px;">
<tr style="background:#fef2f2;">
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Error</th>
</tr>"""
        )
        for log in failed:
            parts.append(
                f"""<tr>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.CompanyName)}</td>
  <td style="padding:8px;border:1px solid #ddd;">{_esc(log.JobTitle)}</td>
  <td style="padding:8px;border:1px solid #ddd;color:#ef4444;">{_esc(log.SkipReason or "—")}</td>
</tr>"""
            )
        parts.append("</table><br/>")

    if not all_logs:
        parts.append('<p style="color:#71717a;">No auto-apply activity today.</p>')

    parts.append(
        """<hr style="margin-top:24px;border:none;border-top:1px solid #e5e7eb;"/>
<p style="color:#9ca3af;font-size:12px;">JobFlow AI — Automated Job Application System</p>
</body></html>"""
    )
    return "".join(parts)
