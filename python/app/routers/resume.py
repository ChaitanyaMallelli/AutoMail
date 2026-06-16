"""ResumeController port — resume CRUD, AI auto-parse, profile update, file view."""
from __future__ import annotations

import json
import logging
import re
from pathlib import Path

from fastapi import APIRouter, Depends, Form, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse, RedirectResponse
from sqlalchemy.orm import Session

from ..config import config
from ..database import get_db
from ..models import Resume, UserProfile
from ..services.factory import build_services
from ..templating import render, set_flash
from ..utils import utcnow

logger = logging.getLogger(__name__)
router = APIRouter()

_RESUME_DIR = config.base_dir / "Resume"


@router.post("/Resume/AutoParse")
async def auto_parse(resumeFile: UploadFile | None = None, db: Session = Depends(get_db)):
    if resumeFile is None or (resumeFile.content_type or "") != "application/pdf":
        return JSONResponse({"success": False, "message": "Please upload a valid PDF file."}, status_code=400)
    data = await resumeFile.read()
    if not data:
        return JSONResponse({"success": False, "message": "Please upload a valid PDF file."}, status_code=400)
    try:
        extraction = await build_services(db).gemini.extract_resume_details_from_pdf(data)
        if not extraction.IsSuccessful:
            return JSONResponse({"success": False, "message": extraction.ErrorMessage}, status_code=400)
        return JSONResponse(
            {
                "success": True,
                "skills": ", ".join(extraction.Skills),
                "experience": extraction.Experience,
                "education": extraction.Education,
                "fullText": extraction.FullText,
            }
        )
    except Exception as ex:  # noqa: BLE001
        logger.error("Error auto-parsing resume: %s", ex)
        return JSONResponse({"success": False, "message": str(ex)}, status_code=500)


@router.get("/Resume")
@router.get("/Resume/Index")
def index(request: Request, db: Session = Depends(get_db)):
    active_resume = db.query(Resume).filter(Resume.IsActive.is_(True)).first()
    all_resumes = db.query(Resume).order_by(Resume.UploadedAt.desc()).all()
    profile = db.query(UserProfile).first()
    return render(
        request,
        "resume/index.html",
        {"title": "Resume & Profile", "resume": active_resume, "resumes": all_resumes, "profile": profile},
    )


@router.post("/Resume/Upload")
async def upload(
    fullText: str = Form(""),
    skills: str = Form(""),
    experience: str = Form(""),
    education: str = Form(""),
    resumeFile: UploadFile | None = None,
    db: Session = Depends(get_db),
):
    try:
        for r in db.query(Resume).all():
            r.IsActive = False

        file_path = None
        if resumeFile is not None:
            data = await resumeFile.read()
            if data:
                _RESUME_DIR.mkdir(parents=True, exist_ok=True)
                file_name = Path(resumeFile.filename or "resume.pdf").name
                (_RESUME_DIR / file_name).write_bytes(data)
                file_path = f"Resume/{file_name}"
                logger.info("Saved uploaded resume file directly under Resume folder: %s", _RESUME_DIR / file_name)

        skills_list = [s.strip() for s in re.split(r"[,\n;]", skills or "") if s.strip()]
        resume = Resume(
            FullText=fullText or "",
            Skills=json.dumps(skills_list),
            Experience=experience,
            Education=education,
            FilePath=file_path,
            IsActive=True,
            UploadedAt=utcnow(),
        )
        db.add(resume)
        db.commit()
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Success", "Resume uploaded successfully and set as default!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error uploading resume: %s", ex)
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Error", f"Failed to upload resume: {ex}")
        return resp


@router.post("/Resume/SetDefault")
async def set_default(id: int = Form(...), db: Session = Depends(get_db)):
    try:
        resumes = db.query(Resume).all()
        if not any(r.Id == id for r in resumes):
            resp = RedirectResponse(url="/Resume/Index", status_code=302)
            set_flash(resp, "Error", "Resume not found.")
            return resp
        for r in resumes:
            r.IsActive = r.Id == id
        db.commit()
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Success", "Default resume updated successfully!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error setting default resume: %s", ex)
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Error", f"Failed to set default: {ex}")
        return resp


@router.post("/Resume/Delete")
async def delete(id: int = Form(...), db: Session = Depends(get_db)):
    try:
        resume = db.query(Resume).filter(Resume.Id == id).first()
        if resume is None:
            resp = RedirectResponse(url="/Resume/Index", status_code=302)
            set_flash(resp, "Error", "Resume not found.")
            return resp
        was_active = resume.IsActive
        if resume.FilePath:
            full = config.base_dir / resume.FilePath.lstrip("/")
            if full.exists():
                full.unlink()
                logger.info("Deleted local resume file: %s", full)
        db.delete(resume)
        db.commit()
        if was_active:
            nxt = db.query(Resume).first()
            if nxt is not None:
                nxt.IsActive = True
                db.commit()
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Success", "Resume deleted successfully.")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error deleting resume: %s", ex)
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Error", f"Failed to delete resume: {ex}")
        return resp


@router.get("/Resume/ViewFile/{id}")
def view_file(id: int, db: Session = Depends(get_db)):
    resume = db.query(Resume).filter(Resume.Id == id).first()
    if resume is None or not resume.FilePath:
        return JSONResponse({"message": "Resume file path is empty."}, status_code=404)
    full = config.base_dir / resume.FilePath.lstrip("/")
    if not full.exists():
        return JSONResponse({"message": f"Resume file not found at path: {full}"}, status_code=404)
    mime = "application/pdf"
    if str(full).lower().endswith(".docx"):
        mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    elif str(full).lower().endswith(".doc"):
        mime = "application/msword"
    return FileResponse(str(full), media_type=mime, filename=full.name)


@router.post("/Resume/UpdateProfile")
async def update_profile(
    fullName: str = Form(""),
    email: str = Form(""),
    phone: str = Form(""),
    linkedInUrl: str = Form(""),
    db: Session = Depends(get_db),
):
    try:
        profile = db.query(UserProfile).first()
        if profile is None:
            profile = UserProfile()
            db.add(profile)
        profile.FullName = fullName
        profile.Email = email
        profile.Phone = phone
        profile.LinkedInUrl = linkedInUrl
        db.commit()
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Success", "Profile updated successfully!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error updating profile: %s", ex)
        resp = RedirectResponse(url="/Resume/Index", status_code=302)
        set_flash(resp, "Error", f"Failed to update profile: {ex}")
        return resp
