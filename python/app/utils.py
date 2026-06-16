"""Shared helpers — IST datetime conversion (ports DateTimeExtensions.cs) and HTML escape."""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

# India Standard Time = UTC+5:30 (no DST)
IST = timezone(timedelta(hours=5, minutes=30))


def utcnow() -> datetime:
    """Timezone-naive UTC now, matching C#'s DateTime.UtcNow used throughout the DB."""
    return datetime.utcnow()


def to_ist(dt: datetime | None) -> datetime | None:
    """Convert a (naive-UTC or aware) datetime to IST."""
    if dt is None:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(IST)


def to_ist_string(dt: datetime | None, fmt: str = "%b %d, %H:%M", show_label: bool = False) -> str:
    """Format a datetime in IST. Returns '' for None. Mirrors ToIstString."""
    ist = to_ist(dt)
    if ist is None:
        return ""
    s = ist.strftime(fmt)
    return f"{s} IST" if show_label else s


def html_escape(text: str | None) -> str:
    """Minimal HTML escape matching TelegramService.EscapeHtml (& < >)."""
    if not text:
        return ""
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
