"""TelegramController port — bot settings page, live status, webhook register/delete."""
from __future__ import annotations

import json
import logging
from pathlib import Path

from fastapi import APIRouter, Depends, Form, Request
from fastapi.responses import JSONResponse, RedirectResponse
from sqlalchemy.orm import Session

from ..config import config
from ..database import get_db
from ..services.factory import build_services
from ..services.telegram_progress_tracker import TelegramProgressTracker as Tracker
from ..templating import render, set_flash

logger = logging.getLogger(__name__)
router = APIRouter()


@router.get("/Telegram")
@router.get("/Telegram/Index")
async def index(request: Request, db: Session = Depends(get_db)):
    bot_token = config.get("Telegram:BotToken") or ""
    webhook_url = config.get("Telegram:WebhookUrl") or ""

    bot_info = None
    webhook_info = None
    if bot_token:
        telegram = build_services(db).telegram
        bot_info = await telegram.get_bot_info()
        webhook_info = await telegram.get_webhook_info()

    return render(
        request,
        "telegram/index.html",
        {
            "title": "Telegram Bot",
            "BotToken": bot_token,
            "ConfiguredWebhookUrl": webhook_url,
            "BotInfo": bot_info,
            "WebhookInfo": webhook_info,
        },
    )


@router.get("/Telegram/Progress")
def progress(request: Request):
    return render(
        request,
        "telegram/progress.html",
        {"title": "Live Telegram Processing", "subtitle": "Real-time step-by-step automation status"},
    )


@router.get("/Telegram/GetLiveStatus")
def get_live_status():
    out = []
    for chat_id, steps in Tracker.all_progress().items():
        out.append({"chatId": chat_id, "steps": steps, "jobId": Tracker.get_latest_job_id(chat_id)})
    return JSONResponse(out)


@router.get("/Telegram/IsExecuting")
def is_executing():
    return JSONResponse({"active": Tracker.is_recent_execution_active()})


@router.post("/Telegram/Register")
async def register(request: Request, webhookUrl: str = Form(""), db: Session = Depends(get_db)):
    if not webhookUrl or not webhookUrl.strip():
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        set_flash(resp, "Error", "Webhook URL cannot be empty.")
        return resp

    webhook_url = webhookUrl.strip()
    if not webhook_url.lower().endswith("/api/telegram/webhook"):
        webhook_url = webhook_url.rstrip("/") + "/api/telegram/webhook"

    if not webhook_url.lower().startswith("https://"):
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        set_flash(
            resp,
            "Error",
            "Telegram webhooks require a secure HTTPS URL (e.g. https://xxxx.ngrok-free.app/api/telegram/webhook).",
        )
        return resp

    try:
        telegram = build_services(db).telegram
        success = await telegram.set_webhook(webhook_url)
        if success:
            # Persist to appsettings.json
            path = config.base_dir / "appsettings.json"
            if path.exists():
                data = json.loads(path.read_text(encoding="utf-8"))
                data.setdefault("Telegram", {})
                data["Telegram"]["WebhookUrl"] = webhook_url
                path.write_text(json.dumps(data, indent=2), encoding="utf-8")
            resp = RedirectResponse(url="/Telegram/Index", status_code=302)
            set_flash(resp, "Success", "Telegram webhook registered successfully!")
            return resp
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        set_flash(resp, "Error", "Failed to register webhook with Telegram. Please check your Bot Token.")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error registering webhook: %s", ex)
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        set_flash(resp, "Error", f"Error registering webhook: {ex}")
        return resp


@router.post("/Telegram/Delete")
async def delete(request: Request, db: Session = Depends(get_db)):
    try:
        telegram = build_services(db).telegram
        success = await telegram.delete_webhook()
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        if success:
            set_flash(resp, "Success", "Telegram webhook unregistered successfully.")
        else:
            set_flash(resp, "Error", "Failed to unregister webhook with Telegram.")
        return resp
    except Exception as ex:  # noqa: BLE001
        logger.error("Error deleting webhook: %s", ex)
        resp = RedirectResponse(url="/Telegram/Index", status_code=302)
        set_flash(resp, "Error", f"Error deleting webhook: {ex}")
        return resp
