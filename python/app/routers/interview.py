"""InterviewController port + the SignalR-equivalent WebSocket for the live Co-Pilot.

The original used SignalR (InterviewHub.SendAudioChunk -> ReceiveHint). Here we expose
a WebSocket at /interviewHub that accepts {base64Audio, jobId} and replies with hints.
"""
from __future__ import annotations

import base64
import logging

from fastapi import APIRouter, Depends, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import RedirectResponse
from sqlalchemy.orm import Session

from ..database import SessionLocal, get_db
from ..models import JobPost
from ..services.factory import build_services
from ..templating import render

logger = logging.getLogger(__name__)
router = APIRouter()


@router.get("/Interview/CoPilot/{id}")
def copilot(request: Request, id: int, db: Session = Depends(get_db)):
    job = db.query(JobPost).filter(JobPost.Id == id).first()
    if job is None:
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    return render(request, "interview/copilot.html", {"title": "Interview Co-Pilot", "job": job})


@router.websocket("/interviewHub")
async def interview_hub(ws: WebSocket):
    await ws.accept()
    try:
        while True:
            data = await ws.receive_json()
            base64_audio = data.get("base64Audio", "")
            job_id = int(data.get("jobId", 0))
            try:
                audio_bytes = base64.b64decode(base64_audio)
                with SessionLocal() as db:
                    job = db.query(JobPost).filter(JobPost.Id == job_id).first()
                    if job is None:
                        continue
                    gemini = build_services(db).gemini
                    response_text = await gemini.process_live_interview_audio(audio_bytes, job)
                if response_text:
                    await ws.send_json({"event": "ReceiveHint", "data": response_text})
            except Exception as ex:  # noqa: BLE001
                await ws.send_json({"event": "ReceiveError", "data": str(ex)})
    except WebSocketDisconnect:
        return
