"""EmailController port — preview, edit, regenerate, status, approve, send, history, export."""
from __future__ import annotations

import io
import logging
from datetime import datetime
from pathlib import Path

from fastapi import APIRouter, Depends, Form, Request
from fastapi.responses import RedirectResponse, StreamingResponse
from sqlalchemy.orm import Session

from ..config import config
from ..database import get_db
from ..models import GeneratedEmail, JobPost, JobStatus, Resume, UserProfile
from ..services.factory import build_services
from ..templating import render, set_flash
from ..utils import utcnow

logger = logging.getLogger(__name__)
router = APIRouter()


@router.get("/Email/Preview/{id}")
def preview(request: Request, id: int, db: Session = Depends(get_db)):
    job = db.query(JobPost).filter(JobPost.Id == id).first()
    if job is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    return render(request, "email/preview.html", {"title": "Email Preview", "job": job, "JobStatus": JobStatus})


@router.post("/Email/Update")
async def update(id: int = Form(...), subject: str = Form(""), body: str = Form(""), recipientEmail: str = Form(""), db: Session = Depends(get_db)):
    email = db.query(GeneratedEmail).filter(GeneratedEmail.JobPostId == id).first()
    if email is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    email.Subject = subject
    email.Body = body
    email.RecipientEmail = recipientEmail
    db.commit()
    resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
    set_flash(resp, "Success", "Email draft updated successfully!")
    return resp


@router.post("/Email/Regenerate")
async def regenerate(id: int = Form(...), tone: str = Form("professional"), db: Session = Depends(get_db)):
    job = db.query(JobPost).filter(JobPost.Id == id).first()
    if job is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    if job.GeneratedEmail is not None:
        db.delete(job.GeneratedEmail)
        db.commit()
    await build_services(db).job_processing.continue_processing(id, tone)
    resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
    set_flash(resp, "Success", f"Email regenerated with {tone} tone!")
    return resp


@router.post("/Email/UpdateStatus")
async def update_status(id: int = Form(...), status: str = Form(...), notes: str | None = Form(None), db: Session = Depends(get_db)):
    job = db.query(JobPost).filter(JobPost.Id == id).first()
    if job is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    try:
        job.Status = JobStatus(status)
    except ValueError:
        pass
    if notes:
        job.ResponseNotes = notes
    db.commit()
    resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
    set_flash(resp, "Success", "Application status updated successfully!")
    return resp


@router.post("/Email/Approve")
async def approve(id: int = Form(...), db: Session = Depends(get_db)):
    email = db.query(GeneratedEmail).filter(GeneratedEmail.JobPostId == id).first()
    if email is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    email.IsApproved = True
    if email.JobPost is not None:
        email.JobPost.Status = JobStatus.Approved
        email.JobPost.UpdatedAt = utcnow()
    db.commit()
    resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
    set_flash(resp, "Success", "Email approved! Ready to send.")
    return resp


@router.post("/Email/Send")
async def send(id: int = Form(...), db: Session = Depends(get_db)):
    return await _send_impl(id, db)


@router.post("/Email/Retry")
async def retry(id: int = Form(...), db: Session = Depends(get_db)):
    return await _send_impl(id, db)


async def _send_impl(id: int, db: Session):
    email = db.query(GeneratedEmail).filter(GeneratedEmail.JobPostId == id).first()
    if email is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)

    profile = db.query(UserProfile).first()
    if profile is None:
        resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
        set_flash(resp, "Error", "User profile not found. Please configure your profile first.")
        return resp

    attachment_path = None
    active_resume = db.query(Resume).filter(Resume.IsActive.is_(True)).first()
    if active_resume is not None and active_resume.FilePath:
        full = config.base_dir / active_resume.FilePath.lstrip("/")
        if full.exists():
            attachment_path = str(full)
        else:
            logger.warning("Resume file not found at resolved path: %s — sending without attachment.", full)

    base_url = config.get("AppBaseUrl")
    services = build_services(db)
    success, error_message = services.email_service.send_email(email, profile, attachment_path, base_url, db=db)

    if success:
        email.IsSent = True
        email.SentAt = utcnow()
        email.ErrorMessage = None
        if email.JobPost is not None:
            email.JobPost.Status = JobStatus.Sent
            email.JobPost.UpdatedAt = utcnow()
        db.commit()
        resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
        set_flash(resp, "Success", "Email sent successfully! 🎉")
        return resp
    else:
        email.RetryCount += 1
        email.ErrorMessage = error_message
        if email.JobPost is not None:
            email.JobPost.Status = JobStatus.Failed
            email.JobPost.UpdatedAt = utcnow()
        db.commit()
        resp = RedirectResponse(url=f"/Email/Preview/{id}", status_code=302)
        set_flash(resp, "Error", f"Failed to send email: {error_message}")
        return resp


@router.get("/Email/History")
def history(request: Request, search: str | None = None, status: str | None = None, db: Session = Depends(get_db)):
    query = db.query(GeneratedEmail)
    if search and search.strip():
        s = f"%{search.strip().lower()}%"
        from sqlalchemy import func

        query = query.outerjoin(JobPost, GeneratedEmail.JobPostId == JobPost.Id).filter(
            func.lower(GeneratedEmail.RecipientEmail).like(s)
            | func.lower(GeneratedEmail.Subject).like(s)
            | func.lower(func.coalesce(JobPost.CompanyName, "")).like(s)
            | func.lower(func.coalesce(JobPost.Role, "")).like(s)
        )
    if status == "sent":
        query = query.filter(GeneratedEmail.IsSent.is_(True))
    elif status == "pending":
        query = query.filter(GeneratedEmail.IsSent.is_(False), GeneratedEmail.IsApproved.is_(False))
    elif status == "approved":
        query = query.filter(GeneratedEmail.IsApproved.is_(True), GeneratedEmail.IsSent.is_(False))
    elif status == "failed":
        query = query.filter(GeneratedEmail.ErrorMessage.isnot(None), GeneratedEmail.IsSent.is_(False))

    emails = query.order_by(GeneratedEmail.CreatedAt.desc()).all()
    return render(
        request,
        "email/history.html",
        {"title": "Email History", "emails": emails, "Search": search, "Status": status},
    )


@router.get("/Email/Export")
def export(db: Session = Depends(get_db)):
    emails = db.query(GeneratedEmail).order_by(GeneratedEmail.CreatedAt.desc()).all()
    lines = ["Company,Role,Recipient Email,Subject,Status,Sent Date,Created Date"]
    for email in emails:
        company = (email.JobPost.CompanyName if email.JobPost else "N/A").replace(",", ";")
        role = (email.JobPost.Role if email.JobPost else "N/A").replace(",", ";")
        recipient = (email.RecipientEmail or "").replace(",", ";")
        subject = (email.Subject or "").replace(",", ";")
        sent_status = "Sent" if email.IsSent else ("Failed" if email.ErrorMessage else ("Approved" if email.IsApproved else "Pending"))
        sent_date = email.SentAt.strftime("%Y-%m-%d %H:%M") if email.SentAt else "N/A"
        created_date = email.CreatedAt.strftime("%Y-%m-%d %H:%M")
        lines.append(f"{company},{role},{recipient},{subject},{sent_status},{sent_date},{created_date}")
    csv_bytes = ("\n".join(lines)).encode("utf-8")
    filename = f"applications_export_{datetime.now():%Y%m%d}.csv"
    return StreamingResponse(
        io.BytesIO(csv_bytes),
        media_type="text/csv",
        headers={"Content-Disposition": f"attachment; filename={filename}"},
    )
