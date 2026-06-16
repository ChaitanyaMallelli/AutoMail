"""JobProcessingService — the main extraction->match->email pipeline (port of JobProcessingService.cs).

Handles text / image / PDF / URL job posts, duplicate detection, resume matching,
recruiter-email discovery, and email draft generation, with Telegram progress updates.
"""
from __future__ import annotations

import logging
import re
import uuid
from pathlib import Path

import httpx
from sqlalchemy import func
from sqlalchemy.orm import Session

from ..config import config
from ..models import GeneratedEmail, JobPost, JobSource, JobStatus, Resume, SourceType, UserProfile
from ..schemas import JobExtractionResult, ResumeMatchResult
from ..utils import utcnow
from .company_email_finder_service import CompanyEmailFinderService
from .gemini_service import GeminiService
from .resume_matching_service import ResumeMatchingService
from .telegram_progress_tracker import TelegramProgressTracker as Tracker

logger = logging.getLogger(__name__)

_URL_RE = re.compile(r"^https?://\S+$", re.IGNORECASE)
_UPLOADS_DIR = config.base_dir / "wwwroot" / "uploads"


class JobProcessingService:
    def __init__(
        self,
        db: Session,
        gemini: GeminiService,
        resume_matching: ResumeMatchingService,
        email_finder: CompanyEmailFinderService,
    ) -> None:
        self._db = db
        self._gemini = gemini
        self._resume_matching = resume_matching
        self._email_finder = email_finder

    @staticmethod
    def contains_url(text: str) -> bool:
        return bool(_URL_RE.match(text.strip()))

    async def process_text(self, text: str, source: JobSource, chat_id: int | None = None, tone: str = "professional") -> int:
        logger.info("Processing text job post from %s", source)
        if chat_id is not None:
            Tracker.reset_progress(chat_id)
            Tracker.update_progress(chat_id, "Step 1: Received job post from Telegram ✅")
            Tracker.update_progress(chat_id, "Step 2: AI extracting details (Company, Role, Skills, Recruiter Email)... ⏳")

        extraction = await self._gemini.extract_job_details_from_text(text)
        if not extraction.IsSuccessful:
            logger.warning("Job extraction failed: %s", extraction.ErrorMessage)
            if chat_id is not None:
                Tracker.update_progress(chat_id, f"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})")
            saved_id = self._save_job_post(extraction, source, SourceType.Text, text, None, chat_id)
            if chat_id is not None:
                Tracker.set_latest_job_id(chat_id, saved_id)
            return saved_id

        if chat_id is not None:
            Tracker.update_progress(chat_id, f"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅")

        job_id = await self._process_extraction(extraction, source, SourceType.Text, text, None, chat_id, tone)
        if chat_id is not None:
            Tracker.set_latest_job_id(chat_id, job_id)
        return job_id

    async def process_image(self, image_bytes: bytes, mime_type: str, source: JobSource, chat_id: int | None = None, tone: str = "professional") -> int:
        logger.info("Processing image job post from %s", source)
        if chat_id is not None:
            Tracker.reset_progress(chat_id)
            Tracker.update_progress(chat_id, "Step 1: Received screenshot from Telegram ✅")
            Tracker.update_progress(chat_id, "Step 2: AI reading image text and extracting job details... ⏳")

        _UPLOADS_DIR.mkdir(parents=True, exist_ok=True)
        file_name = f"{uuid.uuid4()}{self._extension(mime_type)}"
        (_UPLOADS_DIR / file_name).write_bytes(image_bytes)
        relative_path = f"/uploads/{file_name}"

        extraction = await self._gemini.extract_job_details_from_image(image_bytes, mime_type)
        if not extraction.IsSuccessful:
            if chat_id is not None:
                Tracker.update_progress(chat_id, f"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})")
            saved_id = self._save_job_post(extraction, source, SourceType.Image, None, relative_path, chat_id)
            if chat_id is not None:
                Tracker.set_latest_job_id(chat_id, saved_id)
            return saved_id

        if chat_id is not None:
            Tracker.update_progress(chat_id, f"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅")

        job_id = await self._process_extraction(extraction, source, SourceType.Image, None, relative_path, chat_id, tone)
        if chat_id is not None:
            Tracker.set_latest_job_id(chat_id, job_id)
        return job_id

    async def process_pdf(self, pdf_bytes: bytes, source: JobSource, chat_id: int | None = None, tone: str = "professional") -> int:
        logger.info("Processing PDF job post from %s", source)
        if chat_id is not None:
            Tracker.reset_progress(chat_id)
            Tracker.update_progress(chat_id, "Step 1: Received PDF file from Telegram ✅")
            Tracker.update_progress(chat_id, "Step 2: AI extracting details from PDF description... ⏳")

        _UPLOADS_DIR.mkdir(parents=True, exist_ok=True)
        file_name = f"{uuid.uuid4()}.pdf"
        (_UPLOADS_DIR / file_name).write_bytes(pdf_bytes)
        relative_path = f"/uploads/{file_name}"

        extraction = await self._gemini.extract_job_details_from_pdf(pdf_bytes)
        if not extraction.IsSuccessful:
            if chat_id is not None:
                Tracker.update_progress(chat_id, f"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})")
            saved_id = self._save_job_post(extraction, source, SourceType.Pdf, None, relative_path, chat_id)
            if chat_id is not None:
                Tracker.set_latest_job_id(chat_id, saved_id)
            return saved_id

        if chat_id is not None:
            Tracker.update_progress(chat_id, f"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅")

        job_id = await self._process_extraction(extraction, source, SourceType.Pdf, None, relative_path, chat_id, tone)
        if chat_id is not None:
            Tracker.set_latest_job_id(chat_id, job_id)
        return job_id

    async def process_url(self, url: str, source: JobSource, chat_id: int | None = None, tone: str = "professional") -> int:
        logger.info("Processing URL job post from %s: %s", source, url)
        if chat_id is not None:
            Tracker.reset_progress(chat_id)
            Tracker.update_progress(chat_id, "Step 1: Received job URL from Telegram ✅")
            Tracker.update_progress(chat_id, "Step 2: Fetching page content and extracting details... ⏳")

        try:
            headers = {"User-Agent": "Mozilla/5.0 (compatible; AutoMail/1.0)"}
            async with httpx.AsyncClient(timeout=15.0, headers=headers, follow_redirects=True) as client:
                resp = await client.get(url)
                html_content = resp.text

            extraction = await self._gemini.extract_job_details_from_url_content(html_content, url)
            if not extraction.IsSuccessful:
                if chat_id is not None:
                    Tracker.update_progress(chat_id, f"Step 2: AI extraction from URL failed ❌ ({extraction.ErrorMessage})")
                saved_id = self._save_job_post(extraction, source, SourceType.Url, url, None, chat_id)
                if chat_id is not None:
                    Tracker.set_latest_job_id(chat_id, saved_id)
                return saved_id

            if chat_id is not None:
                Tracker.update_progress(chat_id, f"Step 2: AI extracted details from URL! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅")

            job_id = await self._process_extraction(extraction, source, SourceType.Url, url, None, chat_id, tone)
            if chat_id is not None:
                Tracker.set_latest_job_id(chat_id, job_id)
            return job_id
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to fetch or process URL: %s — %s", url, ex)
            if chat_id is not None:
                Tracker.update_progress(chat_id, f"Step 2: Failed to fetch URL ❌ ({ex})")
            raise

    def find_duplicate(self, company_name: str, role: str) -> JobPost | None:
        return (
            self._db.query(JobPost)
            .filter(
                func.lower(JobPost.CompanyName) == company_name.lower(),
                func.lower(JobPost.Role) == role.lower(),
                JobPost.Status != JobStatus.Skipped,
            )
            .order_by(JobPost.CreatedAt.desc())
            .first()
        )

    async def continue_processing(self, job_id: int, tone: str = "professional") -> int:
        job = self._db.query(JobPost).filter(JobPost.Id == job_id).first()
        if job is None:
            return 0
        resume = self._db.query(Resume).filter(Resume.IsActive.is_(True)).first()
        profile = self._db.query(UserProfile).first()
        if resume is None or profile is None:
            return job_id

        extraction = JobExtractionResult(
            CompanyName=job.CompanyName,
            Role=job.Role,
            RequiredSkills=job.RequiredSkills or "",
            RecruiterEmail=job.RecruiterEmail,
            ExperienceRequired=job.ExperienceRequired,
            Location=job.Location,
            RawContent=job.RawContent,
            IsSuccessful=True,
        )
        match_result = self._resume_matching.match(extraction, resume)
        try:
            draft = await self._gemini.generate_email(extraction, resume, profile, match_result, tone)
            if draft.IsSuccessful:
                self._db.add(
                    GeneratedEmail(
                        JobPostId=job.Id,
                        Subject=draft.Subject,
                        Body=draft.Body,
                        RecipientEmail=extraction.RecruiterEmail or draft.RecipientEmail,
                        Tone=tone,
                        CreatedAt=utcnow(),
                    )
                )
                job.Status = JobStatus.EmailGenerated
                job.UpdatedAt = utcnow()
                self._db.commit()
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to generate email for job post %s: %s", job.Id, ex)
        return job_id

    async def _process_extraction(
        self,
        extraction: JobExtractionResult,
        source: JobSource,
        source_type: SourceType,
        raw_content: str | None,
        image_path: str | None,
        chat_id: int | None,
        tone: str,
    ) -> int:
        duplicate = self.find_duplicate(extraction.CompanyName, extraction.Role)
        if duplicate is not None and chat_id is not None:
            Tracker.update_progress(
                chat_id,
                f"Step 2.5: ⚠️ Duplicate found! Already applied to {extraction.CompanyName} - {extraction.Role} "
                f"on {duplicate.CreatedAt:%b %d} (Status: {duplicate.Status.value})",
            )
            logger.info(
                "Duplicate job detected: %s - %s (existing JobId: %s)",
                extraction.CompanyName, extraction.Role, duplicate.Id,
            )

        if chat_id is not None:
            Tracker.update_progress(chat_id, "Step 3: Comparing job requirements with your active Resume... ⏳")

        resume = self._db.query(Resume).filter(Resume.IsActive.is_(True)).first()
        profile = self._db.query(UserProfile).first()

        match_result: ResumeMatchResult | None = None
        if resume is not None:
            match_result = self._resume_matching.match(extraction, resume)

        if chat_id is not None:
            if resume is not None and match_result is not None:
                Tracker.update_progress(
                    chat_id,
                    f"Step 3: Resume match completed: ATS Score: {match_result.AtsScore}%, "
                    f"Skills Match: {match_result.MatchPercentage}% ✅",
                )
                if match_result.MatchPercentage < 30:
                    missing = ", ".join(match_result.MissingSkills[:5]) if match_result.MissingSkills else "various required skills"
                    Tracker.update_progress(chat_id, f"Step 3.5: ⚠️ Low match ({match_result.MatchPercentage}%). Missing: {missing}")
            else:
                Tracker.update_progress(chat_id, "Step 3: Skipped resume matching (No active resume found) ⚠️")

        # Step 3.8: recruiter email discovery
        if not extraction.RecruiterEmail or not extraction.RecruiterEmail.strip():
            if chat_id is not None:
                Tracker.update_progress(chat_id, "Step 3.8: No email in job post — searching company website... ⏳")
            found_email = await self._email_finder.find_recruiter_email(extraction.CompanyName, extraction.Role)
            if found_email and found_email.strip():
                extraction.RecruiterEmail = found_email
                logger.info("Email finder resolved recruiter email: %s", found_email)
                if chat_id is not None:
                    Tracker.update_progress(chat_id, f"Step 3.8: Found recruiter email → {found_email} ✅")
            else:
                logger.warning("Email finder could not find recruiter email for %s.", extraction.CompanyName)
                if chat_id is not None:
                    Tracker.update_progress(chat_id, "Step 3.8: No recruiter email found — you can add it manually on the preview page ⚠️")

        job_post = JobPost(
            CompanyName=extraction.CompanyName,
            Role=extraction.Role,
            RequiredSkills=extraction.RequiredSkills,
            RecruiterEmail=extraction.RecruiterEmail,
            ExperienceRequired=extraction.ExperienceRequired,
            Location=extraction.Location,
            Source=source,
            SourceType=source_type,
            RawContent=raw_content or extraction.RawContent,
            ImagePath=image_path,
            AtsScore=match_result.AtsScore if match_result else 0,
            SkillMatchPercentage=match_result.MatchPercentage if match_result else 0,
            Status=JobStatus.Pending,
            TelegramChatId=chat_id,
            CreatedAt=utcnow(),
        )
        self._db.add(job_post)
        self._db.commit()

        if resume is not None and profile is not None and match_result is not None:
            if chat_id is not None:
                Tracker.update_progress(chat_id, "Step 4: AI generating a professional email draft... ⏳")
            try:
                draft = await self._gemini.generate_email(extraction, resume, profile, match_result, tone)
                if draft.IsSuccessful:
                    self._db.add(
                        GeneratedEmail(
                            JobPostId=job_post.Id,
                            Subject=draft.Subject,
                            Body=draft.Body,
                            RecipientEmail=extraction.RecruiterEmail or draft.RecipientEmail or "",
                            Tone=tone,
                            CreatedAt=utcnow(),
                        )
                    )
                    job_post.Status = JobStatus.EmailGenerated
                    job_post.UpdatedAt = utcnow()
                    self._db.commit()
                    if chat_id is not None:
                        Tracker.update_progress(chat_id, "Step 4: Professional email draft generated successfully! ✅")
                        if chat_id == 999:
                            Tracker.update_progress(chat_id, "Step 5: Email draft ready! Review and approve on dashboard ✅")
                        else:
                            Tracker.update_progress(chat_id, "Step 5: Sending approval request to your Telegram... ⏳")
                elif chat_id is not None:
                    Tracker.update_progress(chat_id, f"Step 4: Email generation failed ❌ ({draft.ErrorMessage})")
            except Exception as ex:  # noqa: BLE001
                logger.error("Failed to generate email for job post %s: %s", job_post.Id, ex)
                if chat_id is not None:
                    Tracker.update_progress(chat_id, f"Step 4: Email generation crashed ❌ ({ex})")
        elif chat_id is not None:
            Tracker.update_progress(chat_id, "Step 4: Skipped email generation (No resume or profile configured) ⚠️")

        logger.info("Job post %s created for %s - %s", job_post.Id, job_post.CompanyName, job_post.Role)
        return job_post.Id

    def _save_job_post(
        self,
        extraction: JobExtractionResult,
        source: JobSource,
        source_type: SourceType,
        raw_content: str | None,
        image_path: str | None,
        chat_id: int | None,
    ) -> int:
        job_post = JobPost(
            CompanyName=extraction.CompanyName or "Unknown",
            Role=extraction.Role or "Unknown",
            RequiredSkills=extraction.RequiredSkills,
            RecruiterEmail=extraction.RecruiterEmail,
            ExperienceRequired=extraction.ExperienceRequired,
            Location=extraction.Location,
            Source=source,
            SourceType=source_type,
            RawContent=raw_content or extraction.RawContent,
            ImagePath=image_path,
            Status=JobStatus.Pending,
            TelegramChatId=chat_id,
            CreatedAt=utcnow(),
        )
        self._db.add(job_post)
        self._db.commit()
        return job_post.Id

    @staticmethod
    def _extension(mime_type: str) -> str:
        return {
            "image/jpeg": ".jpg",
            "image/png": ".png",
            "image/webp": ".webp",
            "image/gif": ".gif",
            "application/pdf": ".pdf",
        }.get(mime_type, ".bin")
