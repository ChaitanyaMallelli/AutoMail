"""TelegramWebhookController port — receives Bot API updates, processes in background."""
from __future__ import annotations

import asyncio
import logging

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from ..database import SessionLocal
from ..services.factory import build_services
from ..utils import utcnow

logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api/telegram")


@router.post("/webhook")
async def webhook(request: Request):
    try:
        body = (await request.body()).decode("utf-8")
        logger.info("Received Telegram webhook update. Raw Body: %s", body)
        if not body or not body.strip():
            logger.warning("Telegram webhook update received an empty body")
            return JSONResponse({})
        try:
            update = await request.json()
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to parse Telegram webhook update JSON. Raw body was: %s", body)
            return JSONResponse({})

        async def _process():
            with SessionLocal() as db:
                try:
                    telegram = build_services(db).telegram
                    await telegram.process_update(update)
                except Exception as ex:  # noqa: BLE001
                    logger.error("Error processing Telegram update in background: %s", ex)

        asyncio.create_task(_process())
        return JSONResponse({})
    except Exception as ex:  # noqa: BLE001
        logger.error("Error receiving Telegram webhook: %s", ex)
        return JSONResponse({})  # Always 200 to Telegram


@router.get("/health")
def health():
    return JSONResponse({"status": "healthy", "timestamp": utcnow().isoformat()})
