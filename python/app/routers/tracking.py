"""TrackingController port — 1x1 open-tracking pixel + Telegram 'email opened' notification."""
from __future__ import annotations

import asyncio
import base64
import logging

from fastapi import APIRouter, Depends
from fastapi.responses import Response
from sqlalchemy.orm import Session

from ..database import SessionLocal, get_db
from ..models import GeneratedEmail
from ..services.factory import build_services
from ..utils import utcnow

logger = logging.getLogger(__name__)
router = APIRouter()

_GIF = base64.b64decode("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")


@router.get("/track/open/{token}")
async def open_pixel(token: str, db: Session = Depends(get_db)):
    email = db.query(GeneratedEmail).filter(GeneratedEmail.TrackingToken == token).first()
    if email is not None and email.OpenedAt is None:
        email.OpenedAt = utcnow()
        db.commit()
        logger.info(
            "Email opened: JobPost %s - %s (%s)",
            email.JobPostId,
            email.JobPost.CompanyName if email.JobPost else None,
            email.JobPost.Role if email.JobPost else None,
        )
        chat_id = email.JobPost.TelegramChatId if email.JobPost else None
        if chat_id and chat_id != 999:
            company = email.JobPost.CompanyName if email.JobPost else "Unknown"
            role = email.JobPost.Role if email.JobPost else "Unknown"

            async def _notify():
                with SessionLocal() as bg_db:
                    telegram = build_services(bg_db).telegram
                    msg = (
                        f"📬 <b>Email Opened!</b>\n\nThe recruiter at <b>{company}</b> just opened your "
                        f"application email for <b>{role}</b>! 🔥\n\nThis is a great sign — be ready to respond."
                    )
                    await telegram.send_reply(chat_id, msg)

            asyncio.create_task(_notify())

    return Response(content=_GIF, media_type="image/gif")
