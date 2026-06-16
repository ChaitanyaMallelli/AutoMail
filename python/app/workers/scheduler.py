"""APScheduler setup — registers the four background jobs with the same cadences
as the original IHostedService workers.
"""
from __future__ import annotations

import logging

from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger
from apscheduler.triggers.interval import IntervalTrigger

from ..config import config
from ..database import SessionLocal
from ..models import UserJobPreferences
from .jobs import follow_up_check, gmail_reply_check, job_scout_cycle, send_daily_report

logger = logging.getLogger(__name__)

_scheduler: AsyncIOScheduler | None = None


def _daily_report_hour() -> int:
    try:
        with SessionLocal() as db:
            prefs = db.query(UserJobPreferences).first()
            return prefs.DailyReportUtcHour if prefs else 15
    except Exception:  # noqa: BLE001
        return 15


def start_scheduler() -> AsyncIOScheduler:
    global _scheduler
    if _scheduler is not None:
        return _scheduler

    scheduler = AsyncIOScheduler(timezone="UTC")

    # JobScoutBackgroundService: every 6 hours (first run ~1 min after startup)
    scheduler.add_job(
        job_scout_cycle,
        IntervalTrigger(hours=6),
        id="job_scout",
        next_run_time=_in_seconds(60),
        max_instances=1,
        coalesce=True,
    )

    # FollowUpBackgroundService: every 1 hour
    scheduler.add_job(follow_up_check, IntervalTrigger(hours=1), id="follow_up", max_instances=1, coalesce=True)

    # GmailReplyMonitorService: every 15 minutes
    scheduler.add_job(gmail_reply_check, IntervalTrigger(minutes=15), id="gmail_reply", max_instances=1, coalesce=True)

    # DailyReportBackgroundService: daily at configured UTC hour
    scheduler.add_job(
        send_daily_report,
        CronTrigger(hour=_daily_report_hour(), minute=0, timezone="UTC"),
        id="daily_report",
        max_instances=1,
        coalesce=True,
    )

    scheduler.start()
    _scheduler = scheduler
    logger.info("Background scheduler started (job_scout/6h, follow_up/1h, gmail_reply/15m, daily_report).")
    return scheduler


def shutdown_scheduler() -> None:
    global _scheduler
    if _scheduler is not None:
        _scheduler.shutdown(wait=False)
        _scheduler = None


def _in_seconds(seconds: int):
    from datetime import datetime, timedelta, timezone

    return datetime.now(timezone.utc) + timedelta(seconds=seconds)
