"""IndeedScraperService — port of IndeedScraperService.cs (headless Indeed India scrape)."""
from __future__ import annotations

import asyncio
import logging
from urllib.parse import quote

from playwright.async_api import async_playwright

from ...models import ScoutedJob

logger = logging.getLogger(__name__)


class IndeedScraperService:
    board_name = "Indeed"

    async def scrape_posts(self, keywords: list[str]) -> list[ScoutedJob]:
        found: list[ScoutedJob] = []
        try:
            async with async_playwright() as pw:
                browser = await pw.chromium.launch(
                    headless=True,
                    args=["--disable-blink-features=AutomationControlled", "--no-sandbox"],
                )
                context = await browser.new_context(
                    user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    viewport={"width": 1366, "height": 768},
                )
                page = await context.new_page()
                await page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")

                seen_urls: set[str] = set()

                for keyword in keywords:
                    encoded = quote(keyword, safe="")
                    search_url = (
                        f"https://in.indeed.com/jobs?q={encoded}&l=Bengaluru%2C+Karnataka&fromage=1&sort=date"
                    )
                    logger.info("Scraping Indeed India for: %s", keyword)
                    try:
                        await page.goto(search_url, timeout=15000)
                        await page.wait_for_load_state("domcontentloaded")
                        await asyncio.sleep(2)
                    except Exception as ex:  # noqa: BLE001
                        logger.warning("Failed to load Indeed search page for %s: %s", keyword, ex)
                        continue

                    for _ in range(2):
                        await page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
                        await asyncio.sleep(1.2)

                    cards = await page.query_selector_all(".job_seen_beacon, .tapItem, [data-testid='job-card']")
                    logger.info("Found %d Indeed job cards for '%s'.", len(cards), keyword)

                    for card in cards[:25]:
                        try:
                            title_el = await card.query_selector(
                                "h2.jobTitle a, [data-testid='job-title'] a, .jcs-JobTitle"
                            )
                            company_el = await card.query_selector(
                                "[data-testid='company-name'], .companyName, .company"
                            )
                            location_el = await card.query_selector(
                                "[data-testid='text-location'], .companyLocation"
                            )
                            desc_el = await card.query_selector(".job-snippet, [data-testid='job-snippet']")

                            title_text = (await title_el.inner_text()).strip() if title_el else ""
                            company_text = (await company_el.inner_text()).strip() if company_el else ""
                            location_text = (await location_el.inner_text()).strip() if location_el else ""
                            desc_text = (await desc_el.inner_text()).strip() if desc_el else ""

                            if not title_text:
                                continue

                            jk = await card.get_attribute("data-jk")
                            if jk:
                                job_url = f"https://in.indeed.com/viewjob?jk={jk}"
                            else:
                                href = (await title_el.get_attribute("href")) if title_el else None
                                if not href:
                                    continue
                                job_url = href if href.startswith("http") else "https://in.indeed.com" + href

                            if job_url in seen_urls:
                                continue
                            seen_urls.add(job_url)

                            raw_text = f"{title_text} at {company_text} in {location_text}. {desc_text}"
                            found.append(
                                ScoutedJob(
                                    LinkedInUrl=job_url,
                                    RawText=raw_text.strip(),
                                    KeywordMatched=keyword,
                                    Board=self.board_name,
                                )
                            )
                        except Exception as ex:  # noqa: BLE001
                            logger.debug("Failed to parse Indeed job card: %s", ex)

                    await asyncio.sleep(3)

                await browser.close()
        except Exception as ex:  # noqa: BLE001
            logger.error("Indeed scraping failed: %s", ex)
        return found
