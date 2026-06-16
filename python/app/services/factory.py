"""Service factory — builds the per-request/per-cycle service graph.

Equivalent to ASP.NET Core's scoped DI container: given a DB Session, wires up
all services so they share that session (one "scope").
"""
from __future__ import annotations

from sqlalchemy.orm import Session

from ..config import config
from .auto_apply_service import AutoApplyService
from .company_email_finder_service import CompanyEmailFinderService
from .direct_apply_service import DirectApplyService
from .email_service import EmailService
from .gemini_service import GeminiService
from .job_processing_service import JobProcessingService
from .job_scout_manager import JobScoutManager
from .resume_matching_service import ResumeMatchingService
from .scrapers import IndeedScraperService, LinkedInScraperService, NaukriScraperService
from .telegram_service import TelegramService


class Services:
    """A built scope of services sharing one DB session (mirrors scoped DI)."""

    def __init__(self, db: Session) -> None:
        self.db = db
        self.gemini = GeminiService()
        self.resume_matching = ResumeMatchingService()
        self.email_service = EmailService()
        self.email_finder = CompanyEmailFinderService(self.gemini)
        self.direct_apply = DirectApplyService()
        self.job_processing = JobProcessingService(db, self.gemini, self.resume_matching, self.email_finder)
        self.telegram = TelegramService(db, self.job_processing, self.email_service, self.gemini)
        self.auto_apply = AutoApplyService(
            db, self.gemini, self.resume_matching, self.email_finder, self.email_service, self.telegram
        )

    def scout_manager(self) -> JobScoutManager:
        # Only LinkedIn is active by default (matches Program.cs registration); the
        # Naukri/Indeed scrapers are available but can be enabled via JobBoards config.
        scrapers: list = [LinkedInScraperService()]
        if (config.get("JobBoards:Naukri:Enabled") or "").lower() == "true":
            scrapers.append(NaukriScraperService())
        if (config.get("JobBoards:Indeed:Enabled") or "").lower() == "true":
            scrapers.append(IndeedScraperService())
        return JobScoutManager(scrapers, self.gemini, self.db, self.telegram, self.auto_apply)


def build_services(db: Session) -> Services:
    return Services(db)
