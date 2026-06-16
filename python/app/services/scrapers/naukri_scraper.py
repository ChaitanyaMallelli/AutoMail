"""NaukriScraperService — port of NaukriScraperService.cs.

Persists a session (cookies) to Data/Sessions/naukri_session.json; opens a browser
for manual login when the session is missing/expired, then scrapes job cards.
"""
from __future__ import annotations

import asyncio
import json
import logging

from playwright.async_api import async_playwright

from ...config import config
from ...models import ScoutedJob

logger = logging.getLogger(__name__)

_SESSION_DIR = config.base_dir / "Data" / "Sessions"
_SESSION_FILE = _SESSION_DIR / "naukri_session.json"


class NaukriScraperService:
    board_name = "Naukri"

    async def scrape_posts(self, keywords: list[str]) -> list[ScoutedJob]:
        found: list[ScoutedJob] = []
        try:
            _SESSION_DIR.mkdir(parents=True, exist_ok=True)
            async with async_playwright() as pw:
                browser = await pw.chromium.launch(
                    headless=False, args=["--disable-blink-features=AutomationControlled"]
                )
                context = await browser.new_context(
                    user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    viewport={"width": 1280, "height": 800},
                )
                await self._load_cookies(context)

                page = await context.new_page()
                await page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")

                await page.goto("https://www.naukri.com/")
                await asyncio.sleep(3)

                if not await self._is_logged_in(page):
                    logger.warning("Naukri session expired or not found. Opening browser for manual login...")
                    logger.info("Please log in to Naukri in the browser window. The app continues once logged in.")
                    await page.goto("https://www.naukri.com/nlogin/login")
                    await asyncio.sleep(3)
                    for _ in range(180):
                        if await self._is_logged_in(page):
                            logger.info("Naukri login detected. Saving session...")
                            await self._save_session(context)
                            break
                        await asyncio.sleep(1)
                    if not await self._is_logged_in(page):
                        logger.error("Naukri login timed out. Skipping Naukri scraping.")
                        await browser.close()
                        return found

                logger.info("Naukri session active. Starting job scraping...")

                for keyword in keywords:
                    encoded = keyword.replace(" ", "-").lower()
                    search_url = f"https://www.naukri.com/{encoded}-jobs-in-bangalore?experience=3"
                    logger.info("Scraping Naukri for: %s", keyword)
                    await page.goto(search_url)
                    try:
                        await page.wait_for_selector(".srp-jobtuple-wrapper, .jobTuple", timeout=10000)
                    except Exception:  # noqa: BLE001
                        logger.warning("No job results found on Naukri for: %s", keyword)
                        continue

                    await asyncio.sleep(2)
                    for _ in range(3):
                        await page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
                        await asyncio.sleep(1.5)

                    cards = await page.query_selector_all(".srp-jobtuple-wrapper, .jobTupleHeader")
                    for card in cards[:30]:
                        try:
                            title = await card.query_selector("a.title, a.jobTitle")
                            company = await card.query_selector(".comp-name, .companyInfo")
                            desc = await card.query_selector(".job-desc, .jobDesc")
                            experience = await card.query_selector(".expwdth, li.experience")

                            title_text = (await title.inner_text()) if title else ""
                            company_text = (await company.inner_text()) if company else ""
                            desc_text = (await desc.inner_text()) if desc else ""
                            exp_text = (await experience.inner_text()) if experience else ""
                            job_url = (await title.get_attribute("href")) if title else ""

                            if not title_text.strip() or not job_url:
                                continue

                            raw_text = f"{title_text} at {company_text}. {desc_text}. Experience: {exp_text}"
                            full_url = job_url if job_url.startswith("http") else "https://www.naukri.com" + job_url
                            found.append(
                                ScoutedJob(
                                    LinkedInUrl=full_url,
                                    RawText=raw_text.strip(),
                                    KeywordMatched=keyword,
                                    Board=self.board_name,
                                )
                            )
                        except Exception as ex:  # noqa: BLE001
                            logger.debug("Failed to parse Naukri job card: %s", ex)

                    logger.info("Found %d jobs on Naukri for '%s'.", len(cards), keyword)
                    await asyncio.sleep(3)

                await self._save_session(context)
                await browser.close()
        except Exception as ex:  # noqa: BLE001
            logger.error("Naukri scraping failed: %s", ex)
        return found

    @staticmethod
    async def _is_logged_in(page) -> bool:
        try:
            el = await page.query_selector(
                ".nI-gNb-user-details, .user-name, [class*='userName'], .view-profile-wrapper"
            )
            return el is not None
        except Exception:  # noqa: BLE001
            return False

    async def _load_cookies(self, context) -> None:
        if not _SESSION_FILE.exists():
            return
        try:
            raw = json.loads(_SESSION_FILE.read_text(encoding="utf-8"))
            cookies = []
            for c in raw:
                cookies.append(
                    {
                        "name": c.get("name", ""),
                        "value": c.get("value", ""),
                        "domain": c.get("domain", ".naukri.com"),
                        "path": c.get("path", "/"),
                        "httpOnly": bool(c.get("httpOnly", False)),
                        "secure": bool(c.get("secure", False)),
                    }
                )
            await context.add_cookies(cookies)
            logger.info("Loaded %d Naukri session cookies.", len(cookies))
        except Exception as ex:  # noqa: BLE001
            logger.warning("Failed to load Naukri session cookies. Will require fresh login: %s", ex)

    async def _save_session(self, context) -> None:
        try:
            cookies = await context.cookies()
            data = [
                {
                    "name": c.get("name"),
                    "value": c.get("value"),
                    "domain": c.get("domain"),
                    "path": c.get("path"),
                    "httpOnly": c.get("httpOnly"),
                    "secure": c.get("secure"),
                }
                for c in cookies
            ]
            _SESSION_DIR.mkdir(parents=True, exist_ok=True)
            _SESSION_FILE.write_text(json.dumps(data, indent=2), encoding="utf-8")
            logger.info("Naukri session saved to %s", _SESSION_FILE)
        except Exception as ex:  # noqa: BLE001
            logger.warning("Failed to save Naukri session: %s", ex)
