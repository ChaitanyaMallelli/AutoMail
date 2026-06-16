"""FastAPI application entry point — ports Program.cs.

Wires up: DB init + seeding, static files, Jinja templates, passcode auth middleware,
all routers, the Telegram connectivity probe, and the background scheduler.
"""
from __future__ import annotations

import asyncio
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles

from .auth import PasscodeAuthMiddleware
from .config import config
from .database import SessionLocal
from .db_init import init_db
from .routers import (
    dashboard,
    email,
    interview,
    login,
    resume,
    telegram,
    telegram_webhook,
    tracking,
    upload,
)
from .services.factory import build_services
from .workers.scheduler import shutdown_scheduler, start_scheduler

logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(name)s: %(message)s")
logger = logging.getLogger("Program")

_BASE = Path(__file__).resolve().parent


async def _telegram_connectivity_probe() -> None:
    """Background probe — surfaces ISP/network blocks clearly (port of the startup check)."""
    try:
        with SessionLocal() as db:
            telegram_svc = build_services(db).telegram
            reachable, detail = await telegram_svc.check_connectivity()
        if reachable:
            logger.info("Telegram connectivity OK — %s", detail)
        else:
            logger.warning("TELEGRAM UNREACHABLE — %s", detail)
    except Exception as ex:  # noqa: BLE001
        logger.warning("Telegram connectivity probe could not run: %s", ex)


@asynccontextmanager
async def lifespan(app: FastAPI):
    # ── Startup ──
    init_db()
    asyncio.create_task(_telegram_connectivity_probe())
    start_scheduler()
    yield
    # ── Shutdown ──
    shutdown_scheduler()


app = FastAPI(title="JobFlow AI", lifespan=lifespan)

# Passcode auth (mirrors PasscodeAuthFilter)
app.add_middleware(PasscodeAuthMiddleware)

# Static files: /static (css/js) and /uploads (job screenshots/pdfs)
app.mount("/static", StaticFiles(directory=str(_BASE / "static")), name="static")
_uploads = _BASE.parent / "wwwroot" / "uploads"
_uploads.mkdir(parents=True, exist_ok=True)
app.mount("/uploads", StaticFiles(directory=str(_uploads)), name="uploads")

# Routers (controllers)
app.include_router(login.router)
app.include_router(dashboard.router)
app.include_router(upload.router)
app.include_router(email.router)
app.include_router(resume.router)
app.include_router(tracking.router)
app.include_router(interview.router)
app.include_router(telegram.router)
app.include_router(telegram_webhook.router)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("app.main:app", host="0.0.0.0", port=5128, reload=False)
