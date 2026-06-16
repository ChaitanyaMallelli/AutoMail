"""TelegramService — bot command/callback handling + Bot API calls (port of TelegramService.cs).

Handles incoming updates (text/URL/photo/document/voice/commands and inline-keyboard
callbacks), drives the job pipeline, and sends messages/keyboards back via the Bot API.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field

import httpx
from sqlalchemy.orm import Session

from ..config import config
from ..models import (
    GeneratedEmail,
    JobPost,
    JobSource,
    JobStatus,
    Resume,
    ScoutedJob,
    ScoutedJobStatus,
    UserProfile,
)
from ..utils import html_escape, utcnow
from .gemini_service import GeminiService
from .job_processing_service import JobProcessingService
from .telegram_progress_tracker import TelegramProgressTracker as Tracker

logger = logging.getLogger(__name__)


@dataclass
class MockInterviewState:
    JobId: int = 0
    ConversationHistory: str = ""


# Active mock interviews keyed by chat id (process-global, like the static ConcurrentDictionary).
_active_interviews: dict[int, MockInterviewState] = {}


class TelegramService:
    def __init__(
        self,
        db: Session,
        job_processing: JobProcessingService,
        email_service,
        gemini: GeminiService,
    ) -> None:
        self._db = db
        self._job_processing = job_processing
        self._email_service = email_service
        self._gemini = gemini
        self._bot_token = config.get("Telegram:BotToken") or ""
        self._base = f"https://api.telegram.org/bot{self._bot_token}"

    # ── Update processing ─────────────────────────────────────────────────────
    async def process_update(self, update: dict) -> None:
        chat_id = 0
        try:
            callback = update.get("callback_query")
            if callback:
                await self._handle_callback_query(callback)
                return

            message = update.get("message")
            if not message:
                return

            Tracker.record_execution_start()
            chat_id = (message.get("chat") or {}).get("id") or 0

            if chat_id != 0:
                profile = self._db.query(UserProfile).first()
                if profile is not None and profile.TelegramChatId != chat_id:
                    profile.TelegramChatId = chat_id
                    self._db.commit()

            photos = message.get("photo")
            if photos:
                file_id = photos[-1].get("file_id")
                if file_id:
                    image_bytes = await self._download_file(file_id)
                    if image_bytes is not None:
                        job_id = await self._job_processing.process_image(
                            image_bytes, "image/jpeg", JobSource.Telegram, chat_id
                        )
                        await self._handle_post_extraction_flow(chat_id, job_id)
                        return

            document = message.get("document")
            if document:
                mime_type = document.get("mime_type") or ""
                file_id = document.get("file_id")
                if file_id:
                    file_bytes = await self._download_file(file_id)
                    if file_bytes is not None:
                        if mime_type == "application/pdf":
                            job_id = await self._job_processing.process_pdf(file_bytes, JobSource.Telegram, chat_id)
                        else:
                            job_id = await self._job_processing.process_image(
                                file_bytes, mime_type, JobSource.Telegram, chat_id
                            )
                        await self._handle_post_extraction_flow(chat_id, job_id)
                        return

            voice = message.get("voice")
            if voice:
                file_id = voice.get("file_id")
                if file_id:
                    await self._handle_voice_message(chat_id, file_id)
                    return

            text = message.get("text")
            if text:
                if text.startswith("/"):
                    await self._handle_command(chat_id, text)
                    return
                if JobProcessingService.contains_url(text):
                    job_id = await self._job_processing.process_url(text.strip(), JobSource.Telegram, chat_id)
                    await self._handle_post_extraction_flow(chat_id, job_id)
                    return
                text_job_id = await self._job_processing.process_text(text, JobSource.Telegram, chat_id)
                await self._handle_post_extraction_flow(chat_id, text_job_id)
                return

            await self.send_reply(chat_id, "⚠️ Please send a text job post, URL, screenshot, or PDF file.")
        except Exception as ex:  # noqa: BLE001
            logger.error("Error processing Telegram update: %s", ex)
            if chat_id != 0:
                await self.send_reply(chat_id, f"❌ <b>Processing Error:</b> {html_escape(str(ex))}")

    async def _handle_post_extraction_flow(self, chat_id: int, job_id: int) -> None:
        job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
        if job is None:
            return
        duplicate = self._job_processing.find_duplicate(job.CompanyName, job.Role)
        if duplicate is not None and duplicate.Id != job_id:
            keyboard = {
                "inline_keyboard": [
                    [
                        {"text": "✅ Apply Anyway", "callback_data": f"duplicate_proceed:{job_id}"},
                        {"text": "❌ Skip", "callback_data": f"duplicate_skip:{job_id}"},
                    ]
                ]
            }
            await self.send_keyboard_reply(
                chat_id,
                f"⚠️ <b>Duplicate Found!</b>\nYou already applied to <b>{html_escape(job.CompanyName)} - "
                f"{html_escape(job.Role)}</b> on {duplicate.CreatedAt:%b %d} (Status: {duplicate.Status.value}).\n\n"
                f"What would you like to do?",
                keyboard,
            )
            return
        await self._proceed_to_smart_filter(chat_id, job_id)

    async def _proceed_to_smart_filter(self, chat_id: int, job_id: int) -> None:
        job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
        if job is None:
            return
        if job.SkillMatchPercentage < 30:
            keyboard = {
                "inline_keyboard": [
                    [{"text": "✅ Apply Anyway", "callback_data": f"lowmatch_proceed:{job_id}"}],
                    [{"text": "📝 Save for Later", "callback_data": f"lowmatch_save:{job_id}"}],
                    [{"text": "❌ Skip", "callback_data": f"lowmatch_skip:{job_id}"}],
                ]
            }
            await self.send_keyboard_reply(
                chat_id,
                f"⚠️ <b>Low Match Alert!</b>\nYour skill match is only {job.SkillMatchPercentage}% for "
                f"{html_escape(job.Role)} at {html_escape(job.CompanyName)}.\n\nDo you want to proceed?",
                keyboard,
            )
            return
        await self._proceed_to_tone_selection(chat_id, job_id)

    async def _proceed_to_tone_selection(self, chat_id: int, job_id: int) -> None:
        keyboard = {
            "inline_keyboard": [
                [
                    {"text": "💼 Professional", "callback_data": f"tone_professional:{job_id}"},
                    {"text": "🔥 Enthusiastic", "callback_data": f"tone_enthusiastic:{job_id}"},
                    {"text": "⚡ Concise", "callback_data": f"tone_concise:{job_id}"},
                ]
            ]
        }
        await self.send_keyboard_reply(
            chat_id, "🎨 <b>Select Email Tone</b>\nChoose the tone for your application email:", keyboard
        )

    async def _send_approval_request(self, chat_id: int, job_id: int) -> None:
        job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
        if job is None or job.GeneratedEmail is None:
            return
        Tracker.update_progress(chat_id, "Step 5: Awaiting approval... ⏳")
        email = job.GeneratedEmail
        message_text = (
            f"📝 <b>Email Ready! ({(email.Tone or '').upper()} Tone)</b>\n\n"
            f"🏢 <b>Company:</b> {html_escape(job.CompanyName)}\n"
            f"💼 <b>Role:</b> {html_escape(job.Role)}\n"
            f"🎯 <b>Match Score:</b> {job.SkillMatchPercentage}%\n\n"
            f"<b>Email Subject:</b> {html_escape(email.Subject)}\n\n"
            f"<b>Body:</b>\n<code>{html_escape(email.Body)}</code>\n\n"
            f"<i>Approve and send?</i>"
        )
        keyboard = {
            "inline_keyboard": [
                [
                    {"text": "✅ Approve & Send", "callback_data": f"approve_send:{job_id}"},
                    {"text": "❌ Reject", "callback_data": f"reject:{job_id}"},
                ]
            ]
        }
        await self.send_keyboard_reply(chat_id, message_text, keyboard)

    async def send_job_alert(self, chat_id: int, job: ScoutedJob) -> None:
        message_text = (
            f"🚨 <b>New LinkedIn Job Found!</b>\n\n"
            f"<b>Keyword:</b> {html_escape(job.KeywordMatched)}\n"
            f"<b>Experience:</b> ✅ 3+ Years Verified\n\n"
            f'<a href="{job.LinkedInUrl}">View Post on LinkedIn</a>\n\n'
            f"<i>Would you like me to extract the details, match your resume, and draft an email?</i>"
        )
        keyboard = {
            "inline_keyboard": [
                [
                    {"text": "👍 Apply", "callback_data": f"scout_apply:{job.Id}"},
                    {"text": "👎 Skip", "callback_data": f"scout_ignore:{job.Id}"},
                ]
            ]
        }
        await self.send_keyboard_reply(chat_id, message_text, keyboard)

    async def _handle_callback_query(self, callback: dict) -> None:
        chat_id = 0
        message_id = 0
        try:
            query_id = callback.get("id")
            message_token = callback.get("message")
            if message_token:
                chat_id = (message_token.get("chat") or {}).get("id") or 0
                message_id = message_token.get("message_id") or 0
            data = callback.get("data") or ""

            if query_id:
                try:
                    async with httpx.AsyncClient(timeout=30.0) as client:
                        await client.post(f"{self._base}/answerCallbackQuery", params={"callback_query_id": query_id})
                except Exception:  # noqa: BLE001
                    pass

            parts = data.split(":")
            if len(parts) < 2:
                return
            action = parts[0]
            try:
                job_id = int(parts[1])
            except ValueError:
                return

            job: JobPost | None = None
            if action not in ("scout_apply", "scout_ignore"):
                job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
                if job is None:
                    return

            if action == "duplicate_proceed":
                await self.edit_message_text(chat_id, message_id, "✅ Proceeding with application...")
                await self._proceed_to_smart_filter(chat_id, job_id)
            elif action == "duplicate_skip":
                job.Status = JobStatus.Skipped
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "❌ Job skipped.")
            elif action == "lowmatch_proceed":
                await self.edit_message_text(chat_id, message_id, "✅ Proceeding despite low match...")
                await self._proceed_to_tone_selection(chat_id, job_id)
            elif action == "lowmatch_save":
                job.Status = JobStatus.SavedForLater
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "📝 Saved to dashboard for later review.")
            elif action == "lowmatch_skip":
                job.Status = JobStatus.Skipped
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "❌ Job skipped due to low match.")
            elif action in ("tone_professional", "tone_enthusiastic", "tone_concise"):
                tone = action.replace("tone_", "")
                await self.edit_message_text(chat_id, message_id, f"⏳ Generating email with {tone} tone...")
                await self._job_processing.continue_processing(job_id, tone)
                await self._send_approval_request(chat_id, job_id)
            elif action == "approve_send":
                await self._process_approval_and_send(chat_id, message_id, job)
            elif action == "reject":
                job.Status = JobStatus.Failed
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "❌ Email draft rejected.")
            elif action == "followup_send":
                await self.edit_message_text(chat_id, message_id, "⏳ Generating follow-up email...")
                profile = self._db.query(UserProfile).first()
                if profile is not None:
                    draft = await self._gemini.generate_followup_email(job, profile)
                    if draft.IsSuccessful:
                        follow_up = GeneratedEmail(
                            JobPostId=job.Id,
                            Subject=draft.Subject,
                            Body=draft.Body,
                            RecipientEmail=job.RecruiterEmail or "",
                            Tone="followup",
                        )
                        success, err = self._email_service.send_email(follow_up, profile, None, db=self._db)
                        if success:
                            job.Status = JobStatus.FollowUpSent
                            self._db.commit()
                            await self.edit_message_text(chat_id, message_id, "🚀 Follow-up email sent successfully!")
                            await self._prompt_for_response_tracking(chat_id, job.Id)
                        else:
                            await self.edit_message_text(chat_id, message_id, f"❌ Failed to send follow-up: {err}")
            elif action == "followup_gotresponse":
                await self.edit_message_text(chat_id, message_id, "Awesome! Did you get an interview?")
                await self._prompt_for_response_tracking(chat_id, job.Id)
            elif action == "followup_dismiss":
                await self.edit_message_text(chat_id, message_id, "Dismissed reminder.")
            elif action == "scout_apply":
                scouted = self._db.query(ScoutedJob).filter(ScoutedJob.Id == job_id).first()
                if scouted is not None:
                    scouted.Status = ScoutedJobStatus.Applied
                    self._db.commit()
                    await self.edit_message_text(
                        chat_id, message_id, f"⏳ <b>Extracting Details...</b>\nFrom: {scouted.LinkedInUrl}"
                    )
                    new_job_id = await self._job_processing.process_url(scouted.LinkedInUrl, JobSource.Telegram, chat_id)
                    await self._handle_post_extraction_flow(chat_id, new_job_id)
            elif action == "scout_ignore":
                ignored = self._db.query(ScoutedJob).filter(ScoutedJob.Id == job_id).first()
                if ignored is not None:
                    ignored.Status = ScoutedJobStatus.IgnoredByGemini
                    self._db.commit()
                    await self.edit_message_text(chat_id, message_id, "❌ Post ignored.")
            elif action == "response_interview":
                job.Status = JobStatus.InterviewScheduled
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "🎉 Awesome! Marked as Interview Scheduled. Good luck!")
            elif action == "response_rejected":
                job.Status = JobStatus.Rejected
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "😞 Sorry to hear that. Marked as Rejected. Keep trying!")
            elif action == "response_ghosted":
                job.Status = JobStatus.Ghosted
                self._db.commit()
                await self.edit_message_text(chat_id, message_id, "👻 Marked as Ghosted.")
        except Exception as ex:  # noqa: BLE001
            logger.error("Error handling callback query: %s", ex)

    async def _process_approval_and_send(self, chat_id: int, message_id: int, job: JobPost) -> None:
        await self.edit_message_text(chat_id, message_id, "⏳ Sending email...")
        attachment_path = None
        active_resume = self._db.query(Resume).filter(Resume.IsActive.is_(True)).first()
        if active_resume is not None and active_resume.FilePath:
            if active_resume.FilePath.lower().startswith("resume"):
                attachment_path = str(config.base_dir / active_resume.FilePath.replace("/", "\\"))
            else:
                attachment_path = str(config.base_dir / "wwwroot" / active_resume.FilePath.lstrip("/"))

        profile = self._db.query(UserProfile).first()
        if profile is None or job.GeneratedEmail is None:
            return

        success, error_message = self._email_service.send_email(
            job.GeneratedEmail, profile, attachment_path, db=self._db
        )
        if success:
            job.GeneratedEmail.IsSent = True
            job.GeneratedEmail.SentAt = utcnow()
            job.Status = JobStatus.Sent
            self._db.commit()
            await self.edit_message_text(
                chat_id, message_id, f"🚀 <b>Email Sent Successfully!</b>\nTo: {html_escape(job.GeneratedEmail.RecipientEmail)}"
            )
        else:
            job.Status = JobStatus.Failed
            job.GeneratedEmail.ErrorMessage = error_message
            self._db.commit()
            await self.edit_message_text(
                chat_id, message_id, f"❌ <b>Failed to Send Email:</b>\n<code>{html_escape(error_message)}</code>"
            )

    async def _prompt_for_response_tracking(self, chat_id: int, job_id: int) -> None:
        keyboard = {
            "inline_keyboard": [
                [{"text": "📞 Interview Scheduled", "callback_data": f"response_interview:{job_id}"}],
                [{"text": "❌ Rejected", "callback_data": f"response_rejected:{job_id}"}],
                [{"text": "👻 Ghosted / No Response", "callback_data": f"response_ghosted:{job_id}"}],
            ]
        }
        await self.send_keyboard_reply(chat_id, "Any updates on this application?", keyboard)

    async def _handle_voice_message(self, chat_id: int, file_id: str) -> None:
        state = _active_interviews.get(chat_id)
        if state is None:
            await self.send_reply(
                chat_id,
                "⚠️ I received a voice note, but we are not currently in a mock interview. "
                "Send `/mockinterview [JobId]` to start one!",
            )
            return
        job = self._db.query(JobPost).filter(JobPost.Id == state.JobId).first()
        profile = self._db.query(UserProfile).first()
        if job is None or profile is None:
            await self.send_reply(chat_id, "⚠️ Error: Could not find job or profile context.")
            return
        await self.send_reply(chat_id, "🎙️ <i>Listening...</i>")
        audio_bytes = await self._download_file(file_id)
        if audio_bytes is None:
            await self.send_reply(chat_id, "❌ Failed to download your voice note.")
            return
        response_text = await self._gemini.process_mock_interview_audio(
            audio_bytes, job, profile, state.ConversationHistory
        )
        state.ConversationHistory += f"\nApplicant: [Voice Note]\nRecruiter: {response_text}\n"
        await self.send_reply(
            chat_id,
            f"🤖 <b>Recruiter:</b>\n\n{response_text}\n\n"
            f"<i>(Reply with another voice note to continue, or /stopinterview to end)</i>",
        )

    async def _handle_command(self, chat_id: int, command: str) -> None:
        parts = [p for p in command.split(" ") if p]
        cmd = parts[0].lower().split("@")[0]
        if cmd in ("/start", "/help"):
            await self.send_reply(
                chat_id,
                "👋 Welcome to JobFlow AI!\n\nSend me:\n🔗 Job URLs (LinkedIn, Indeed)\n📝 Text job posts\n"
                "📸 Screenshots\n📄 PDF job descriptions\n\nI'll extract details, match your resume, and write the email!",
            )
        elif cmd == "/mockinterview":
            if len(parts) < 2 or not parts[1].isdigit():
                await self.send_reply(chat_id, "⚠️ Please provide a Job ID. Example: `/mockinterview 5`")
                return
            job_id = int(parts[1])
            job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
            if job is None:
                await self.send_reply(chat_id, "❌ Job not found.")
                return
            _active_interviews[chat_id] = MockInterviewState(JobId=job_id)
            await self.send_reply(
                chat_id,
                f"🎙️ <b>Mock Interview Started!</b>\nRole: {job.Role} at {job.CompanyName}\n\n"
                f"Send a <b>Voice Message</b> saying hello to begin!",
            )
        elif cmd == "/stopinterview":
            if _active_interviews.pop(chat_id, None) is not None:
                await self.send_reply(chat_id, "🛑 Mock Interview ended. Great job practicing!")
            else:
                await self.send_reply(chat_id, "You are not in a mock interview.")

    # ── Bot API calls ─────────────────────────────────────────────────────────
    async def _download_file(self, file_id: str) -> bytes | None:
        try:
            async with httpx.AsyncClient(timeout=60.0) as client:
                resp = await client.get(f"{self._base}/getFile", params={"file_id": file_id})
                file_path = (resp.json().get("result") or {}).get("file_path")
                if not file_path:
                    return None
                file_resp = await client.get(f"https://api.telegram.org/file/bot{self._bot_token}/{file_path}")
                return file_resp.content
        except Exception:  # noqa: BLE001
            return None

    async def send_reply(self, chat_id: int, message: str) -> None:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                await client.post(
                    f"{self._base}/sendMessage",
                    data={"chat_id": str(chat_id), "text": message, "parse_mode": "HTML"},
                )
        except Exception:  # noqa: BLE001
            pass

    async def send_keyboard_reply(self, chat_id: int, message: str, reply_markup: dict) -> None:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                await client.post(
                    f"{self._base}/sendMessage",
                    json={"chat_id": chat_id, "text": message, "parse_mode": "HTML", "reply_markup": reply_markup},
                )
        except Exception:  # noqa: BLE001
            pass

    async def edit_message_text(self, chat_id: int, message_id: int, new_text: str) -> None:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                await client.post(
                    f"{self._base}/editMessageText",
                    json={"chat_id": chat_id, "message_id": message_id, "text": new_text, "parse_mode": "HTML"},
                )
        except Exception:  # noqa: BLE001
            pass

    async def get_bot_info(self) -> dict | None:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.get(f"{self._base}/getMe")
                return resp.json()
        except Exception:  # noqa: BLE001
            return None

    async def get_webhook_info(self) -> dict | None:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.get(f"{self._base}/getWebhookInfo")
                return resp.json()
        except Exception:  # noqa: BLE001
            return None

    async def set_webhook(self, url: str) -> bool:
        try:
            import json as _json

            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.get(
                    f"{self._base}/setWebhook",
                    params={"url": url, "allowed_updates": _json.dumps(["message", "callback_query"])},
                )
                return bool(resp.json().get("ok"))
        except Exception:  # noqa: BLE001
            return False

    async def delete_webhook(self) -> bool:
        try:
            async with httpx.AsyncClient(timeout=30.0) as client:
                resp = await client.get(f"{self._base}/deleteWebhook")
                return bool(resp.json().get("ok"))
        except Exception:  # noqa: BLE001
            return False

    async def check_connectivity(self) -> tuple[bool, str]:
        """Fast startup probe (port of CheckConnectivityAsync). Fails fast + loud on ISP blocks."""
        if not self._bot_token or not self._bot_token.strip():
            return False, "No Telegram bot token configured (set Telegram:BotToken)."
        try:
            async with httpx.AsyncClient(timeout=8.0) as client:
                resp = await client.get(f"{self._base}/getMe")
                data = resp.json()
                if data.get("ok") is True:
                    username = (data.get("result") or {}).get("username")
                    return True, f"Telegram API reachable (bot @{username})."
                return False, f"Telegram API rejected the request: {resp.text}"
        except httpx.TimeoutException:
            return False, (
                "Timed out reaching api.telegram.org. This usually means an ISP/network block "
                "(e.g. Airtel DNS RPZ redirect, or IP-level firewall) rather than a code issue. "
                "Fix: connect through a VPN / Cloudflare WARP, or switch to a different network."
            )
        except Exception as ex:  # noqa: BLE001
            return False, f"Cannot reach api.telegram.org ({ex}). Likely an ISP/network block — try a VPN / different network."
