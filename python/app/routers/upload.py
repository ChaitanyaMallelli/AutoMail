"""UploadController port — manual job submission (text / URL / file)."""
from __future__ import annotations

import logging

from fastapi import APIRouter, Depends, Form, Request, UploadFile
from fastapi.responses import RedirectResponse
from sqlalchemy.orm import Session

from ..database import get_db
from ..models import JobSource
from ..services.factory import build_services
from ..templating import render, set_flash

logger = logging.getLogger(__name__)
router = APIRouter()


@router.get("/Upload")
@router.get("/Upload/Index")
def index(request: Request):
    return render(request, "upload/index.html", {"title": "New Job", "subtitle": "Submit a job post for processing"})


@router.post("/Upload/ProcessText")
async def process_text(request: Request, jobText: str = Form(""), db: Session = Depends(get_db)):
    if not jobText or not jobText.strip():
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", "Please enter job post text.")
        return resp
    try:
        job_id = await build_services(db).job_processing.process_text(jobText, JobSource.Upload)
        resp = RedirectResponse(url=f"/Email/Preview/{job_id}", status_code=302)
        set_flash(resp, "Success", "Job post processed successfully!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error processing text upload: %s", ex)
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", f"Processing failed: {ex}")
        return resp


@router.post("/Upload/ProcessUrl")
async def process_url(request: Request, jobUrl: str = Form(""), db: Session = Depends(get_db)):
    if not jobUrl or not jobUrl.strip():
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", "Please enter a job URL.")
        return resp
    try:
        job_id = await build_services(db).job_processing.process_url(jobUrl, JobSource.Upload)
        resp = RedirectResponse(url=f"/Email/Preview/{job_id}", status_code=302)
        set_flash(resp, "Success", "Job URL processed successfully!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error processing URL upload: %s", ex)
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", f"Processing failed: {ex}")
        return resp


@router.post("/Upload/ProcessFile")
async def process_file(request: Request, file: UploadFile | None = None, db: Session = Depends(get_db)):
    if file is None:
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", "Please select a file to upload.")
        return resp
    data = await file.read()
    if not data:
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", "Please select a file to upload.")
        return resp
    try:
        svc = build_services(db).job_processing
        content_type = file.content_type or ""
        if content_type == "application/pdf":
            job_id = await svc.process_pdf(data, JobSource.Upload)
        elif content_type.startswith("image/"):
            job_id = await svc.process_image(data, content_type, JobSource.Upload)
        else:
            resp = RedirectResponse(url="/Upload/Index", status_code=302)
            set_flash(resp, "Error", "Unsupported file type. Please upload an image or PDF.")
            return resp
        resp = RedirectResponse(url=f"/Email/Preview/{job_id}", status_code=302)
        set_flash(resp, "Success", "File processed successfully!")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error processing file upload: %s", ex)
        resp = RedirectResponse(url="/Upload/Index", status_code=302)
        set_flash(resp, "Error", f"Processing failed: {ex}")
        return resp
