"""AutoApplyService — fully automated apply pipeline (port of AutoApplyService.cs).

Filters a scouted job through preferences (experience, work mode, location,
excluded companies, ATS), generates + sends the email, logs the result, and
notifies via Telegram.
"""
from __future__ import annotations

import logging
import re
from pathlib import Path

import httpx
from sqlalchemy import func
from sqlalchemy.orm import Session

from ..config import config
from ..models import (
    AutoApplyLog,
    AutoApplyStatus,
    GeneratedEmail,
    JobPost,
    JobSource,
    JobStatus,
    Resume,
    ScoutedJob,
    SourceType,
    UserJobPreferences,
    UserProfile,
)
from ..utils import to_ist, utcnow
from .company_email_finder_service import CompanyEmailFinderService
from .email_service import EmailService
from .gemini_service import GeminiService
from .resume_matching_service import ResumeMatchingService

logger = logging.getLogger(__name__)


class AutoApplyService:
    def __init__(
        self,
        db: Session,
        gemini: GeminiService,
        resume_matching: ResumeMatchingService,
        email_finder: CompanyEmailFinderService,
        email_service: EmailService,
        telegram_service,
    ) -> None:
        self._db = db
        self._gemini = gemini
        self._resume_matching = resume_matching
        self._email_finder = email_finder
        self._email_service = email_service
        self._telegram = telegram_service

    async def process_scouted_job(self, scouted_job: ScoutedJob, chat_id: int) -> AutoApplyLog:
        log = AutoApplyLog(
            JobUrl=scouted_job.LinkedInUrl,
            JobId=self.extract_job_id(scouted_job.LinkedInUrl),
            Platform=scouted_job.Board or "LinkedIn",
            AppliedAt=utcnow(),
        )
        try:
            prefs = self._db.query(UserJobPreferences).first() or UserJobPreferences()

            if not prefs.AutoApplyEnabled:
                log.CompanyName = "Unknown"
                log.JobTitle = "Unknown"
                return await self._skip(log, "Auto-apply is disabled", chat_id, notify=False)

            fast_dup = self._fast_duplicate_check(scouted_job.LinkedInUrl, log.JobId)
            if fast_dup is not None:
                log.CompanyName = "Unknown"
                log.JobTitle = "Unknown"
                return await self._skip(log, fast_dup, chat_id, notify=False)

            try:
                headers = {"User-Agent": "Mozilla/5.0 (compatible; AutoMail/1.0)"}
                async with httpx.AsyncClient(timeout=20.0, headers=headers, follow_redirects=True) as client:
                    resp = await client.get(scouted_job.LinkedInUrl)
                    html_content = resp.text
            except Exception as ex:  # noqa: BLE001
                log.CompanyName = "Unknown"
                log.JobTitle = "Unknown"
                logger.warning("AutoApply: failed to fetch URL %s: %s", scouted_job.LinkedInUrl, ex)
                return await self._fail(log, f"URL fetch failed: {ex}", chat_id)

            extraction = await self._gemini.extract_job_details_from_url_content(html_content, scouted_job.LinkedInUrl)
            if not extraction.IsSuccessful:
                log.CompanyName = "Unknown"
                log.JobTitle = "Unknown"
                return await self._fail(log, f"Extraction failed: {extraction.ErrorMessage}", chat_id)

            log.CompanyName = extraction.CompanyName or "Unknown"
            log.JobTitle = extraction.Role or "Unknown"
            log.Location = extraction.Location
            log.ExperienceRequired = extraction.ExperienceRequired
            log.WorkMode = self._detect_work_mode(scouted_job.RawText or html_content)

            full_dup = self._full_duplicate_check(log.CompanyName, log.JobTitle)
            if full_dup is not None:
                return await self._skip(log, full_dup, chat_id, notify=False)

            exp_min, exp_max = self.parse_experience_range(extraction.ExperienceRequired)
            if exp_min is None:
                return await self._skip(log, "Experience requirement unknown — cannot verify fit", chat_id, notify=True)

            if exp_max < prefs.MinExperienceYears or exp_min > prefs.MaxExperienceYears:
                return await self._skip(
                    log,
                    f"Experience {extraction.ExperienceRequired} outside preferred {prefs.MinExperienceYears}–{prefs.MaxExperienceYears} yrs",
                    chat_id,
                    notify=True,
                )

            if prefs.PreferredWorkModes and prefs.PreferredWorkModes.strip() and log.WorkMode and log.WorkMode.strip():
                preferred = [m.strip() for m in prefs.PreferredWorkModes.split(",") if m.strip()]
                if not any(m.lower() in log.WorkMode.lower() for m in preferred):
                    return await self._skip(
                        log,
                        f"Work mode '{log.WorkMode}' not in preferred list ({prefs.PreferredWorkModes})",
                        chat_id,
                        notify=True,
                    )

            if prefs.PreferredLocations and prefs.PreferredLocations.strip() and extraction.Location and extraction.Location.strip():
                preferred_locs = [l.strip() for l in prefs.PreferredLocations.split(",") if l.strip()]
                job_loc = extraction.Location.lower()
                if not any(l.lower() in job_loc for l in preferred_locs):
                    return await self._skip(
                        log,
                        f"Location '{extraction.Location}' not in preferred list ({prefs.PreferredLocations})",
                        chat_id,
                        notify=True,
                    )

            if prefs.ExcludedCompanies and prefs.ExcludedCompanies.strip():
                excluded = [e.strip() for e in prefs.ExcludedCompanies.split(",") if e.strip()]
                if any(e.lower() in log.CompanyName.lower() for e in excluded):
                    return await self._skip(log, f"Company '{log.CompanyName}' is in excluded list", chat_id, notify=False)

            resume = self._db.query(Resume).filter(Resume.IsActive.is_(True)).first()
            profile = self._db.query(UserProfile).first()
            if resume is None or profile is None:
                return await self._fail(log, "No active resume or user profile configured", chat_id)

            match_result = self._resume_matching.match(extraction, resume)
            if match_result.AtsScore < prefs.MinAtsScore:
                return await self._skip(
                    log, f"ATS score {match_result.AtsScore}% below minimum {prefs.MinAtsScore}%", chat_id, notify=True
                )

            if not extraction.RecruiterEmail or not extraction.RecruiterEmail.strip():
                found = await self._email_finder.find_recruiter_email(extraction.CompanyName, extraction.Role)
                if found and found.strip():
                    extraction.RecruiterEmail = found

            job_post = JobPost(
                CompanyName=extraction.CompanyName or log.CompanyName,
                Role=extraction.Role or log.JobTitle,
                RequiredSkills=extraction.RequiredSkills,
                RecruiterEmail=extraction.RecruiterEmail,
                ExperienceRequired=extraction.ExperienceRequired,
                Location=extraction.Location,
                Source=JobSource.Upload,
                SourceType=SourceType.Url,
                RawContent=scouted_job.LinkedInUrl,
                AtsScore=match_result.AtsScore,
                SkillMatchPercentage=match_result.MatchPercentage,
                Status=JobStatus.Pending,
                CreatedAt=utcnow(),
            )
            self._db.add(job_post)
            self._db.commit()
            log.JobPostId = job_post.Id

            draft = await self._gemini.generate_email(extraction, resume, profile, match_result, "professional")
            if not draft.IsSuccessful:
                return await self._fail(log, f"Email generation failed: {draft.ErrorMessage}", chat_id)

            generated_email = GeneratedEmail(
                JobPostId=job_post.Id,
                Subject=draft.Subject,
                Body=draft.Body,
                RecipientEmail=extraction.RecruiterEmail or draft.RecipientEmail or "",
                Tone="professional",
                CreatedAt=utcnow(),
            )
            self._db.add(generated_email)
            job_post.Status = JobStatus.EmailGenerated
            job_post.UpdatedAt = utcnow()
            self._db.commit()

            if generated_email.RecipientEmail and generated_email.RecipientEmail.strip():
                resume_path = None
                if resume.FilePath:
                    normalized = resume.FilePath.lstrip("/").replace("/", str(Path("/")).strip("/") or "/")
                    full = config.base_dir / resume.FilePath.lstrip("/")
                    if full.exists():
                        resume_path = str(full)

                app_base_url = config.get("AppBaseUrl")
                sent, send_error = self._email_service.send_email(
                    generated_email, profile, resume_path, app_base_url, db=self._db
                )
                if sent:
                    job_post.Status = JobStatus.Sent
                    job_post.UpdatedAt = utcnow()
                    self._db.commit()
                    log.EmailSent = True
                    log.Status = AutoApplyStatus.Applied
                    logger.info("AutoApply: sent email for %s – %s", log.CompanyName, log.JobTitle)
                else:
                    return await self._fail(log, f"SMTP send failed: {send_error}", chat_id)
            else:
                log.Status = AutoApplyStatus.Applied
                log.SkipReason = "No recruiter email found — draft saved for manual review"
                logger.info("AutoApply: draft saved (no recruiter email) for %s – %s", log.CompanyName, log.JobTitle)

            self._save_log(log)

            if chat_id != 0:
                await self._send_telegram_safe(chat_id, self._build_apply_notification(log, scouted_job.LinkedInUrl))
                log.TelegramNotified = True
                self._db.commit()

            return log
        except Exception as ex:  # noqa: BLE001
            logger.error("AutoApply: unhandled error for %s: %s", scouted_job.LinkedInUrl, ex)
            log.CompanyName = log.CompanyName or "Unknown"
            log.JobTitle = log.JobTitle or "Unknown"
            return await self._fail(log, str(ex), chat_id)

    # ── Duplicate checks ──────────────────────────────────────────────────────
    def _fast_duplicate_check(self, url: str, job_id: str | None) -> str | None:
        if self._db.query(AutoApplyLog).filter(AutoApplyLog.JobUrl == url).first():
            return "Duplicate URL already in AutoApplyLogs"
        if job_id and self._db.query(AutoApplyLog).filter(AutoApplyLog.JobId == job_id).first():
            return f"Duplicate Job ID '{job_id}' already in AutoApplyLogs"
        if (
            self._db.query(JobPost)
            .filter(JobPost.RawContent == url, JobPost.Status != JobStatus.Skipped)
            .first()
        ):
            return "URL already manually applied via JobPosts"
        return None

    def _full_duplicate_check(self, company: str, role: str) -> str | None:
        cl, rl = company.lower(), role.lower()
        if (
            self._db.query(AutoApplyLog)
            .filter(func.lower(AutoApplyLog.CompanyName) == cl, func.lower(AutoApplyLog.JobTitle) == rl)
            .first()
        ):
            return f"Already auto-applied to {company} – {role}"
        if (
            self._db.query(JobPost)
            .filter(func.lower(JobPost.CompanyName) == cl, func.lower(JobPost.Role) == rl, JobPost.Status != JobStatus.Skipped)
            .first()
        ):
            return f"Already applied to {company} – {role} via main pipeline"
        return None

    async def _skip(self, log: AutoApplyLog, reason: str, chat_id: int, notify: bool) -> AutoApplyLog:
        log.Status = AutoApplyStatus.Skipped
        log.SkipReason = reason
        self._save_log(log)
        logger.info("AutoApply SKIP [%s – %s]: %s", log.CompanyName, log.JobTitle, reason)
        if notify and chat_id != 0:
            await self._send_telegram_safe(
                chat_id, f"⏭️ *Auto-Skip*: {self._esc_md(log.CompanyName)} — {self._esc_md(log.JobTitle)}\n_{self._esc_md(reason)}_"
            )
            log.TelegramNotified = True
            self._db.commit()
        return log

    async def _fail(self, log: AutoApplyLog, reason: str, chat_id: int) -> AutoApplyLog:
        log.Status = AutoApplyStatus.Failed
        log.SkipReason = reason
        self._save_log(log)
        logger.warning("AutoApply FAIL [%s – %s]: %s", log.CompanyName, log.JobTitle, reason)
        if chat_id != 0:
            await self._send_telegram_safe(
                chat_id, f"❌ *Auto-Apply Failed*: {self._esc_md(log.CompanyName)} — {self._esc_md(log.JobTitle)}\n_{self._esc_md(reason)}_"
            )
            log.TelegramNotified = True
            self._db.commit()
        return log

    def _save_log(self, log: AutoApplyLog) -> None:
        if log.Id is None or log.Id == 0:
            if log not in self._db:
                self._db.add(log)
        self._db.commit()

    def _build_apply_notification(self, log: AutoApplyLog, url: str) -> str:
        lines = []
        if log.EmailSent:
            lines.append(f"✅ *Auto-Applied*: {self._esc_md(log.JobTitle)} at *{self._esc_md(log.CompanyName)}*")
        else:
            lines.append(f"📋 *Draft Ready* \\(no email found\\): {self._esc_md(log.JobTitle)} at *{self._esc_md(log.CompanyName)}*")
        if log.Location and log.Location.strip():
            lines.append(f"📍 {self._esc_md(log.Location)}")
        if log.WorkMode and log.WorkMode.strip():
            lines.append(f"💼 {self._esc_md(log.WorkMode)}")
        if log.ExperienceRequired and log.ExperienceRequired.strip():
            lines.append(f"🎯 {self._esc_md(log.ExperienceRequired)}")
        ist = to_ist(utcnow())
        lines.append(f"🕐 {ist:%b %d, %H:%M} IST")
        lines.append(f"🔗 [View Job]({url})")
        return "\n".join(lines) + "\n"

    async def _send_telegram_safe(self, chat_id: int, message: str) -> None:
        try:
            await self._telegram.send_reply(chat_id, message)
        except Exception as ex:  # noqa: BLE001
            logger.warning("AutoApply: Telegram notify failed: %s", ex)

    # ── Static helpers ────────────────────────────────────────────────────────
    @staticmethod
    def extract_job_id(url: str | None) -> str | None:
        if not url or not url.strip():
            return None
        m = re.search(r"/jobs/view/(\d+)", url)
        if m:
            return m.group(1)
        m = re.search(r"JID([A-Za-z0-9]+)", url)
        if m:
            return m.group(1)
        return None

    @staticmethod
    def parse_experience_range(text: str | None) -> tuple[int | None, int | None]:
        if not text or not text.strip():
            return None, None
        t = text.lower()
        rng = re.search(r"(\d+)\s*[-–to]+\s*(\d+)\s*y", t)
        if rng:
            return int(rng.group(1)), int(rng.group(2))
        plus = re.search(r"(\d+)\s*\+", t)
        if plus:
            return int(plus.group(1)), 99
        min_word = re.search(r"minimum\s+(\d+)", t)
        if min_word:
            return int(min_word.group(1)), 99
        single = re.search(r"(\d+)\s*y", t)
        if single:
            v = int(single.group(1))
            return v, v
        return None, None

    @staticmethod
    def _detect_work_mode(text: str) -> str:
        t = text.lower()
        modes = []
        if "remote" in t:
            modes.append("Remote")
        if "hybrid" in t:
            modes.append("Hybrid")
        if "onsite" in t or "on-site" in t or "in office" in t or "in-office" in t:
            modes.append("Onsite")
        return "/".join(modes) if modes else "Not specified"

    @staticmethod
    def _esc_md(s: str | None) -> str:
        if not s:
            return ""
        return s.replace("_", "\\_").replace("*", "\\*").replace("[", "\\[").replace("`", "\\`")
