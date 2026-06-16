"""Jinja2 templates instance + shared filters (IST formatting, flash messages).

Flash messages mirror ASP.NET Core's TempData using a signed session cookie.
"""
from __future__ import annotations

from pathlib import Path

from fastapi.templating import Jinja2Templates
from starlette.requests import Request

from .utils import to_ist, to_ist_string

_TEMPLATES_DIR = Path(__file__).resolve().parent / "templates"

templates = Jinja2Templates(directory=str(_TEMPLATES_DIR))


def _ist_filter(value, fmt: str = "%b %d, %H:%M"):
    ist = to_ist(value)
    return ist.strftime(fmt) if ist else ""


templates.env.filters["ist"] = _ist_filter
templates.env.filters["ist_string"] = to_ist_string


def _scout_sidebar_jobs():
    """Jinja global — replicates ScoutSidebarViewComponent (latest 8 scouted jobs)."""
    from .database import SessionLocal
    from .models import ScoutedJob

    with SessionLocal() as db:
        return db.query(ScoutedJob).order_by(ScoutedJob.CreatedAt.desc()).limit(8).all()


templates.env.globals["scout_sidebar_jobs"] = _scout_sidebar_jobs


def set_flash(response, kind: str, message: str) -> None:
    """kind = 'Success' | 'Error'. Stored in a short-lived cookie consumed on next render."""
    response.set_cookie(f"flash_{kind}", message, max_age=10, path="/")


def pop_flash(request: Request) -> dict[str, str]:
    out: dict[str, str] = {}
    for kind in ("Success", "Error"):
        val = request.cookies.get(f"flash_{kind}")
        if val:
            out[kind] = val
    return out


def render(request: Request, template: str, context: dict | None = None):
    """Render a template, injecting flash messages + request automatically."""
    ctx = dict(context or {})
    ctx["request"] = request
    ctx.setdefault("flash", pop_flash(request))
    response = templates.TemplateResponse(template, ctx)
    # Clear consumed flash cookies
    for kind in ("Success", "Error"):
        if request.cookies.get(f"flash_{kind}"):
            response.delete_cookie(f"flash_{kind}", path="/")
    return response
