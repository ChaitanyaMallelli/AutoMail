"""JobScoutManager — orchestrates scraping across boards, dedup, Gemini relevance
filter, and auto-apply/alert (port of JobScoutManager.cs).
"""
from __future__ import annotations

import logging

from sqlalchemy.orm import Session

from ..models import ScoutedJob, ScoutedJobStatus, UserJobPreferences, UserProfile, JobPost
from ..playwright_runner import run_playwright
from .auto_apply_service import AutoApplyService
from .gemini_service import GeminiService

logger = logging.getLogger(__name__)

SEARCH_KEYWORDS = [
    "hiring for dotnet developer",
    "dotnet developer",
    ".net developer",
    ".net developer banglore",
    ".net developer in dubai",
]


class JobScoutManager:
    def __init__(
        self,
        scrapers: list,
        gemini: GeminiService,
        db: Session,
        telegram_service,
        auto_apply: AutoApplyService,
    ) -> None:
        self._scrapers = scrapers
        self._gemini = gemini
        self._db = db
        self._telegram = telegram_service
        self._auto_apply = auto_apply

    async def run_scout_cycle(self) -> None:
        chat_id = (
            self._db.query(UserProfile.TelegramChatId)
            .filter(UserProfile.TelegramChatId.isnot(None), UserProfile.TelegramChatId != 0)
            .scalar()
            or self._db.query(JobPost.TelegramChatId)
            .filter(JobPost.TelegramChatId.isnot(None), JobPost.TelegramChatId != 0)
            .scalar()
            or 0
        )

        all_scraped: list[ScoutedJob] = []
        for scraper in self._scrapers:
            logger.info("Starting %s scrape...", scraper.board_name)
            try:
                # Playwright needs a Proactor loop on Windows — run it in an isolated thread/loop.
                jobs = await run_playwright(lambda s=scraper: s.scrape_posts(SEARCH_KEYWORDS))
                all_scraped.extend(jobs)
                logger.info("%s: %d raw posts scraped.", scraper.board_name, len(jobs))
            except Exception as ex:  # noqa: BLE001
                logger.error("%s scraper failed: %s", scraper.board_name, ex)

        if not all_scraped:
            logger.info("No posts found across all boards.")
            return

        # Deduplicate by URL within this batch (preserve first)
        seen_in_batch: set[str] = set()
        new_jobs: list[ScoutedJob] = []
        for j in all_scraped:
            if j.LinkedInUrl not in seen_in_batch:
                seen_in_batch.add(j.LinkedInUrl)
                new_jobs.append(j)

        logger.info("Total: %d raw → %d unique.", len(all_scraped), len(new_jobs))

        # Bulk dedup against DB
        candidate_urls = {j.LinkedInUrl for j in new_jobs}
        already_seen_urls = {
            row[0]
            for row in self._db.query(ScoutedJob.LinkedInUrl).filter(ScoutedJob.LinkedInUrl.in_(candidate_urls)).all()
        }
        candidate_texts = {j.RawText for j in new_jobs if j.RawText is not None}
        already_seen_texts: set[str] = set()
        if candidate_texts:
            already_seen_texts = {
                row[0]
                for row in self._db.query(ScoutedJob.RawText)
                .filter(ScoutedJob.RawText.isnot(None), ScoutedJob.RawText.in_(candidate_texts))
                .all()
            }

        fresh = [
            j
            for j in new_jobs
            if j.LinkedInUrl not in already_seen_urls
            and (j.RawText is None or j.RawText not in already_seen_texts)
        ]
        logger.info("%d new (unseen) posts to filter.", len(fresh))
        if not fresh:
            return

        texts = [j.RawText or "" for j in fresh]
        try:
            relevance_results = await self._gemini.batch_is_post_relevant(texts)
        except Exception as ex:  # noqa: BLE001
            logger.error("Batch relevance check failed entirely — skipping cycle: %s", ex)
            return

        auto_apply_enabled = (
            self._db.query(UserJobPreferences.AutoApplyEnabled).scalar()
        )
        if auto_apply_enabled is None:
            auto_apply_enabled = True

        to_save: list[ScoutedJob] = []
        to_alert: list[ScoutedJob] = []
        for i, job in enumerate(fresh):
            is_relevant = i < len(relevance_results) and relevance_results[i]
            job.JobId = AutoApplyService.extract_job_id(job.LinkedInUrl)
            if is_relevant:
                logger.info("✅ MATCH: %s", job.LinkedInUrl)
                job.Status = ScoutedJobStatus.SentToTelegram
                to_alert.append(job)
            else:
                logger.info("❌ IGNORED: %s", job.LinkedInUrl)
                job.Status = ScoutedJobStatus.IgnoredByGemini
            to_save.append(job)

        self._db.add_all(to_save)
        self._db.commit()
        logger.info("Saved %d jobs. %d match(es).", len(to_save), len(to_alert))

        for job in to_alert:
            if auto_apply_enabled:
                try:
                    await self._auto_apply.process_scouted_job(job, chat_id)
                except Exception as ex:  # noqa: BLE001
                    logger.error("AutoApply failed for %s: %s", job.LinkedInUrl, ex)
            else:
                if chat_id != 0:
                    try:
                        await self._telegram.send_job_alert(chat_id, job)
                    except Exception as ex:  # noqa: BLE001
                        logger.warning("Failed to send Telegram alert for %s: %s", job.LinkedInUrl, ex)

        logger.info("Scout cycle complete.")
