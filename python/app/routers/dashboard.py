"""DashboardController port — dashboard list/stats, scout logs, run-scout, apply-scouted."""
from __future__ import annotations

import asyncio
import logging
import math
import re

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from sqlalchemy import func
from sqlalchemy.orm import Session

from ..database import SessionLocal, get_db
from ..models import GeneratedEmail, JobPost, JobSource, JobStatus, Resume, ScoutedJob, ScoutedJobStatus, UserProfile
from ..services.factory import build_services
from ..services.telegram_progress_tracker import TelegramProgressTracker as Tracker
from ..templating import render
from ..utils import to_ist
from ..config import config

logger = logging.getLogger(__name__)
router = APIRouter()

PAGE_SIZE = 20


@router.get("/")
@router.get("/Dashboard")
@router.get("/Dashboard/Index")
def index(
    request: Request,
    search: str | None = None,
    status: str | None = None,
    sort: str | None = None,
    page: int = 1,
    db: Session = Depends(get_db),
):
    query = db.query(JobPost)

    if search and search.strip():
        s = f"%{search.strip().lower()}%"
        query = query.filter(
            func.lower(JobPost.CompanyName).like(s)
            | func.lower(JobPost.Role).like(s)
            | func.lower(func.coalesce(JobPost.Location, "")).like(s)
            | func.lower(func.coalesce(JobPost.RequiredSkills, "")).like(s)
        )

    if status and status.strip():
        try:
            query = query.filter(JobPost.Status == JobStatus(status))
        except ValueError:
            pass

    if sort == "company":
        query = query.order_by(JobPost.CompanyName.asc())
    elif sort == "role":
        query = query.order_by(JobPost.Role.asc())
    elif sort == "ats":
        query = query.order_by(JobPost.AtsScore.desc())
    elif sort == "match":
        query = query.order_by(JobPost.SkillMatchPercentage.desc())
    else:
        query = query.order_by(JobPost.CreatedAt.desc())

    total_filtered = query.count()
    total_pages = math.ceil(total_filtered / PAGE_SIZE) if total_filtered else 0
    page = max(1, min(page, max(1, total_pages)))
    job_posts = query.offset((page - 1) * PAGE_SIZE).limit(PAGE_SIZE).all()

    rows = db.query(JobPost.Status, func.count(JobPost.Id)).group_by(JobPost.Status).all()
    counts = {st: c for st, c in rows}

    def count(s: JobStatus) -> int:
        return counts.get(s, 0)

    total_jobs = sum(c for st, c in counts.items() if st != JobStatus.Skipped)
    pending_jobs = count(JobStatus.Pending) + count(JobStatus.EmailGenerated)
    interviews = count(JobStatus.InterviewScheduled)
    offers = count(JobStatus.Offered)
    rejected = count(JobStatus.Rejected)
    sent_jobs = (
        count(JobStatus.Sent) + count(JobStatus.FollowUpSent) + interviews + rejected + offers + count(JobStatus.Ghosted)
    )
    failed_jobs = count(JobStatus.Failed)

    avg_match = 0
    if total_jobs > 0:
        avg = (
            db.query(func.avg(JobPost.SkillMatchPercentage)).filter(JobPost.Status != JobStatus.Skipped).scalar()
        )
        avg_match = int(avg or 0)

    response_rate = round((interviews + offers + rejected) / sent_jobs * 100) if sent_jobs > 0 else 0

    ctx = {
        "title": "Dashboard",
        "subtitle": "Track and manage your job applications",
        "jobs": job_posts,
        "JobStatus": JobStatus,
        "stats": {
            "TotalJobs": total_jobs,
            "PendingJobs": pending_jobs,
            "SentJobs": sent_jobs,
            "FailedJobs": failed_jobs,
            "Interviews": interviews,
            "Offers": offers,
            "ResponseRate": response_rate,
            "AvgMatch": avg_match,
        },
        "Search": search,
        "Status": status,
        "Sort": sort,
        "Page": page,
        "TotalPages": total_pages,
        "TotalFiltered": total_filtered,
    }
    return render(request, "dashboard/index.html", ctx)


@router.get("/Dashboard/GetScoutLogs")
def get_scout_logs(db: Session = Depends(get_db)):
    jobs = db.query(ScoutedJob).order_by(ScoutedJob.CreatedAt.desc()).limit(100).all()
    return JSONResponse(
        [
            {
                "id": j.Id,
                "createdAt": to_ist(j.CreatedAt).strftime("%b %d, %H:%M"),
                "keyword": j.KeywordMatched,
                "status": j.Status.value,
                "rawPreview": (j.RawText[:90] + "...") if j.RawText and len(j.RawText) > 90 else j.RawText,
                "url": j.LinkedInUrl,
                "board": j.Board or "LinkedIn",
            }
            for j in jobs
        ]
    )


@router.post("/Dashboard/RunScoutNow")
async def run_scout_now(db: Session = Depends(get_db)):
    try:
        services = build_services(db)
        await services.scout_manager().run_scout_cycle()
        return JSONResponse({"success": True})
    except Exception as ex:  # noqa: BLE001
        logger.error("Failed to run manual scout cycle: %s", ex)
        return JSONResponse({"success": False, "message": str(ex)}, status_code=500)


@router.post("/Dashboard/ApplyScoutedJob")
async def apply_scouted_job(id: int, db: Session = Depends(get_db)):
    scouted = db.query(ScoutedJob).filter(ScoutedJob.Id == id).first()
    if scouted is None:
        return JSONResponse({"success": False, "message": "Scouted job not found."}, status_code=404)
    if not scouted.LinkedInUrl or not scouted.LinkedInUrl.strip():
        return JSONResponse({"success": False, "message": "No URL available for this scouted job."}, status_code=400)

    raw_text = scouted.RawText or ""
    email_match = re.search(r"[\w.+\-]+@[\w\-]+\.[a-zA-Z]{2,}", raw_text, re.IGNORECASE)
    phone_match = re.search(r"\+?\d[\d\s\-\(\)]{7,}\d", raw_text)
    has_contact = bool(email_match or phone_match)
    contact_email = email_match.group(0) if email_match else None

    board = scouted.Board or "LinkedIn"
    is_external = board != "LinkedIn"

    scouted.Status = ScoutedJobStatus.Applied
    db.commit()

    Tracker.reset_progress(999)
    url = scouted.LinkedInUrl
    scouted_id = scouted.Id

    async def _background():
        with SessionLocal() as bg_db:
            services = build_services(bg_db)
            profile = bg_db.query(UserProfile).first()
            resume = bg_db.query(Resume).filter(Resume.IsActive.is_(True)).first()
            resume_path = None
            if resume and resume.FilePath:
                full = config.base_dir / resume.FilePath.lstrip("/")
                if full.exists():
                    resume_path = str(full)

            sj = bg_db.query(ScoutedJob).filter(ScoutedJob.Id == scouted_id).first()

            if is_external and profile is not None and sj is not None:
                Tracker.update_progress(999, f"Step 1: Launching Direct Apply on {board}... ⏳")
                try:
                    success, message = await services.direct_apply.apply(sj, profile, resume_path)
                    Tracker.update_progress(
                        999,
                        f"Step 1: Applied directly on {board} ✅" if success else f"Step 1: Direct apply on {board} — {message} ⚠️",
                    )
                except Exception as ex:  # noqa: BLE001
                    logger.error("Direct apply failed for scouted job %s: %s", scouted_id, ex)
                    Tracker.update_progress(999, f"Step 1: Direct apply crashed ❌ ({ex})")

            should_run_email = (not is_external) or has_contact
            if should_run_email:
                reason = "LinkedIn job" if not is_external else f"contact info found in post ({contact_email or 'phone'})"
                Tracker.update_progress(999, f"Step 2: {reason} — generating personalized email... ⏳")
                try:
                    await services.job_processing.process_url(url, JobSource.Upload, 999)
                except Exception as ex:  # noqa: BLE001
                    logger.error("Email pipeline failed for scouted job %s: %s", scouted_id, ex)
                    Tracker.update_progress(999, f"Step 2: Email pipeline crashed ❌ ({ex})")
            else:
                Tracker.update_progress(999, "Step 2: No contact info in post — skipping email pipeline ℹ️")
                Tracker.update_progress(999, "Step 5: Email draft ready! Review and approve on dashboard ✅")

    asyncio.create_task(_background())
    return JSONResponse({"success": True, "redirectUrl": "/Telegram/Progress"})
