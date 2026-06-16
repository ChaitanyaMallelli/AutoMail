"""LoginController port."""
from __future__ import annotations

from datetime import timedelta

from fastapi import APIRouter, Form, Request
from fastapi.responses import RedirectResponse

from ..config import config
from ..templating import render

router = APIRouter()


def _passcode() -> str:
    return config.get("AppPasscode") or "password123"


@router.get("/Login")
@router.get("/Login/Index")
def index(request: Request):
    if request.cookies.get("AuthPasscode") == _passcode():
        return RedirectResponse(url="/Dashboard/Index", status_code=302)
    return render(request, "login.html", {"title": "Sign In"})


@router.post("/Login/Submit")
def submit(request: Request, passcode: str = Form(...)):
    if passcode == _passcode():
        resp = RedirectResponse(url="/Dashboard/Index", status_code=302)
        resp.set_cookie(
            "AuthPasscode",
            _passcode(),
            max_age=int(timedelta(days=7).total_seconds()),
            httponly=True,
            samesite="lax",
            path="/",
        )
        return resp
    return render(request, "login.html", {"title": "Sign In", "error": "Incorrect passcode. Please try again."})


@router.get("/Login/Logout")
def logout():
    resp = RedirectResponse(url="/Login", status_code=302)
    resp.delete_cookie("AuthPasscode", path="/")
    return resp
