"""Passcode auth — port of PasscodeAuthFilter.cs as a FastAPI middleware.

Bypasses the same paths (webhook, login, tracking, live-status, static) and
otherwise requires the AuthPasscode cookie to equal the configured passcode.
"""
from __future__ import annotations

from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import RedirectResponse

from .config import config

_BYPASS_PREFIXES = (
    "/api/telegram",
    "/login",
    "/track/",
    "/telegram/isexecuting",
    "/telegram/getlivestatus",
    "/telegram/progress",
    "/css/",
    "/js/",
    "/lib/",
    "/static/",
    "/uploads/",
    "/docs",
    "/openapi.json",
)


class PasscodeAuthMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        path = (request.url.path or "").lower()
        if any(path.startswith(p) for p in _BYPASS_PREFIXES):
            return await call_next(request)

        configured = config.get("AppPasscode") or "password123"
        if request.cookies.get("AuthPasscode") == configured:
            return await call_next(request)

        return RedirectResponse(url="/Login", status_code=302)
