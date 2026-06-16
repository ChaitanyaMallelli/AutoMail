"""CompanyEmailFinderService — find a recruiter/HR email (port of CompanyEmailFinderService.cs).

Strategy 1: scrape common career/contact pages. Strategy 2: ask Gemini for a likely pattern.
"""
from __future__ import annotations

import logging
import re

import httpx

from .gemini_service import GeminiService

logger = logging.getLogger(__name__)

_EMAIL_RE = re.compile(r"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", re.IGNORECASE)

_BLOCKED_PREFIXES = {
    "noreply", "no-reply", "donotreply", "do-not-reply",
    "support", "info", "contact", "hello", "admin", "webmaster",
}


class CompanyEmailFinderService:
    def __init__(self, gemini: GeminiService) -> None:
        self._gemini = gemini

    async def find_recruiter_email(self, company_name: str, role: str) -> str | None:
        logger.info("Searching for recruiter email: %s — %s", company_name, role)
        try:
            candidates = await self._scrape_company_website(company_name)
            if candidates:
                best = self._pick_best_email(candidates, company_name)
                if best:
                    logger.info("Found recruiter email via website scrape: %s", best)
                    return best

            gemini_email = await self._ask_gemini_for_email(company_name, role)
            if gemini_email:
                logger.info("Gemini suggested recruiter email: %s", gemini_email)
                return gemini_email
        except Exception as ex:  # noqa: BLE001
            logger.warning("Email finder failed for %s: %s", company_name, ex)
        return None

    async def _scrape_company_website(self, company_name: str) -> list[str]:
        emails: list[str] = []
        try:
            slug = (
                company_name.lower()
                .replace(" ", "")
                .replace("pvt", "")
                .replace("ltd", "")
                .replace(".", "")
                .strip()
            )
            urls_to_try = [
                f"https://{slug}.com/careers",
                f"https://{slug}.com/contact",
                f"https://{slug}.in/careers",
                f"https://www.{slug}.com/careers",
                f"https://www.{slug}.com/contact-us",
            ]
            headers = {
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"
            }
            async with httpx.AsyncClient(timeout=10.0, headers=headers, follow_redirects=True) as client:
                for url in urls_to_try:
                    try:
                        resp = await client.get(url)
                        found = [
                            m.group(0).lower()
                            for m in _EMAIL_RE.finditer(resp.text)
                            if not self._is_blocked(m.group(0).lower())
                        ]
                        found = list(dict.fromkeys(found))  # distinct, preserve order
                        emails.extend(found)
                        if found:
                            break
                    except Exception:  # noqa: BLE001
                        continue
        except Exception as ex:  # noqa: BLE001
            logger.debug("Website scrape failed for %s: %s", company_name, ex)
        return list(dict.fromkeys(emails))

    async def _ask_gemini_for_email(self, company_name: str, role: str) -> str | None:
        try:
            prompt = f"""You are helping find a recruiter's email address for a job application.

Company: {company_name}
Role: {role}

Based on common HR email patterns for Indian tech companies, suggest the most likely
recruiter or HR email address for this company.

Rules:
- Only return a real-looking email address, nothing else
- Use patterns like: hr@company.com, careers@company.com, recruit@company.com
- If the company name suggests it's a large MNC (TCS, Infosys, Wipro, etc.), return "N/A"
- If you cannot make a reasonable guess, return "N/A"
- Return ONLY the email address or "N/A", no explanation"""
            result = (await self._gemini.call_gemini_public(prompt) or "").strip()
            if not result or result == "N/A":
                return None
            if not _EMAIL_RE.search(result):
                return None
            if self._is_blocked(result):
                return None
            return result
        except Exception:  # noqa: BLE001
            return None

    @staticmethod
    def _pick_best_email(emails: list[str], company_name: str) -> str | None:
        priority = ["hr@", "recruit", "career", "hiring", "talent", "jobs@"]
        for prefix in priority:
            for e in emails:
                if prefix in e:
                    return e
        return emails[0] if emails else None

    @staticmethod
    def _is_blocked(email: str) -> bool:
        local = email.split("@")[0]
        return local in _BLOCKED_PREFIXES or "example.com" in email or "test.com" in email
