"""LinkedInScraperService — port of LinkedInScraperService.cs (incl. the SDUI fixes).

Logs into LinkedIn, searches /search/results/content for each keyword (past 24h),
and extracts posts. Because LinkedIn moved to server-driven UI with randomized
classes and no inline permalinks, we anchor on role="listitem" +
[data-testid="expandable-text-box"] and resolve each permalink via the post's
control menu -> "Copy link to post" -> clipboard. On 0 results we dump a
screenshot + HTML to scrape-debug/ for inspection.
"""
from __future__ import annotations

import asyncio
import logging
import re
from pathlib import Path

from playwright.async_api import TimeoutError as PWTimeoutError, async_playwright

from ...config import config
from ...models import ScoutedJob

logger = logging.getLogger(__name__)

_POST_SELECTOR = "div[role='listitem']:has([data-testid='expandable-text-box'])"


class LinkedInScraperService:
    board_name = "LinkedIn"

    def __init__(self) -> None:
        self._email = config.get("LinkedIn:Email") or ""
        self._password = config.get("LinkedIn:Password") or ""

    async def scrape_posts(self, keywords: list[str]) -> list[ScoutedJob]:
        found: list[ScoutedJob] = []

        if not self._email or not self._password:
            logger.error("LinkedIn credentials not found in appsettings.")
            return found

        async with async_playwright() as pw:
            browser = await pw.chromium.launch(headless=False)
            context = await browser.new_context(
                user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                viewport={"width": 1280, "height": 720},
                permissions=["clipboard-read", "clipboard-write"],
            )
            page = await context.new_page()
            await page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")

            try:
                await self._login(page)
            except Exception as ex:  # noqa: BLE001
                logger.error("Failed to login. Current URL: %s. Taking screenshot...", page.url)
                try:
                    await page.screenshot(path="login_error.png")
                except Exception:  # noqa: BLE001
                    pass
                await browser.close()
                return found

            for keyword in keywords:
                logger.info("Searching LinkedIn Posts for: %s", keyword)
                encoded = _url_encode(keyword)
                search_url = (
                    f"https://www.linkedin.com/search/results/content/?datePosted=%22past-24h%22&keywords={encoded}"
                )
                await page.goto(search_url)
                await page.wait_for_load_state("domcontentloaded")

                try:
                    await page.wait_for_selector(_POST_SELECTOR, timeout=15000)
                except PWTimeoutError:
                    pass

                post_elements = await page.query_selector_all(_POST_SELECTOR)
                logger.info("Initially found %d posts for keyword %s.", len(post_elements), keyword)

                if not post_elements:
                    await self._capture_debug_state(page, keyword)
                    logger.warning(
                        "No search results found for %s. Saved screenshot + HTML to scrape-debug/ for inspection. "
                        "Common causes: LinkedIn changed result class names, a 'restricted activity' wall, "
                        "or a genuine empty result set.",
                        keyword,
                    )
                    continue

                # Scroll to load more (up to 50 posts / 10 scrolls)
                max_scrolls = 10
                scrolls = 0
                while len(post_elements) < 50 and scrolls < max_scrolls:
                    logger.info(
                        "Scrolling to load more posts... (Current: %d, Scroll: %d/%d)",
                        len(post_elements), scrolls + 1, max_scrolls,
                    )
                    await page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
                    await asyncio.sleep(2.5)
                    current = await page.query_selector_all(_POST_SELECTOR)
                    if len(current) == len(post_elements):
                        await page.evaluate("window.scrollBy(0, -300)")
                        await asyncio.sleep(0.5)
                        await page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
                        await asyncio.sleep(2.0)
                        current = await page.query_selector_all(_POST_SELECTOR)
                        if len(current) == len(post_elements):
                            logger.info("No more posts available to load for this search.")
                            break
                    post_elements = current
                    scrolls += 1

                logger.info("Finished loading posts for keyword %s. Total found: %d", keyword, len(post_elements))

                for element in post_elements:
                    try:
                        text_el = await element.query_selector("[data-testid='expandable-text-box']")
                        raw_text = (await text_el.inner_text()) if text_el else ""
                        if not raw_text or not raw_text.strip():
                            continue
                        url = await self._get_post_url_via_menu(page, element)
                        if not url:
                            logger.debug("Could not resolve a permalink for a '%s' post; skipping it.", keyword)
                            continue
                        found.append(
                            ScoutedJob(
                                LinkedInUrl=url,
                                RawText=raw_text,
                                KeywordMatched=keyword,
                                Board=self.board_name,
                            )
                        )
                    except Exception as ex:  # noqa: BLE001
                        logger.warning("Failed to extract a post for keyword %s; continuing: %s", keyword, ex)

                await asyncio.sleep(_rand_delay())

            await browser.close()

        return found

    async def _login(self, page) -> None:
        logger.info("Logging into LinkedIn...")
        await page.goto("https://www.linkedin.com/login")
        await page.wait_for_load_state("domcontentloaded")
        await asyncio.sleep(3)

        if "feed" not in page.url and "checkpoint" not in page.url:
            email_sel = (
                "input#username:visible, input#session_key:visible, input[name='session_key']:visible, "
                "input[type='email']:visible, input[autocomplete='username']:visible"
            )
            pass_sel = (
                "input#password:visible, input#session_password:visible, input[name='session_password']:visible, "
                "input[type='password']:visible, input[autocomplete='current-password']:visible"
            )
            await page.wait_for_selector(email_sel, timeout=10000)
            await page.fill(email_sel, self._email)
            await page.fill(pass_sel, self._password)
            await page.press(pass_sel, "Enter")

        logger.info("Waiting for home feed to load...")
        feed_loaded = False
        for _ in range(60):
            if "feed" in page.url or "checkpoint" in page.url:
                feed_loaded = True
                break
            await asyncio.sleep(1)
        if not feed_loaded:
            raise TimeoutError("Failed to redirect to LinkedIn feed page or checkpoint after login.")

        if "checkpoint" in page.url:
            logger.warning("LinkedIn security checkpoint detected. Please complete verification in the browser window...")
            for _ in range(90):
                if "feed" in page.url:
                    logger.info("Security checkpoint successfully completed!")
                    break
                await asyncio.sleep(1)

        await asyncio.sleep(5)
        logger.info("Login successful and feed loaded.")

    async def _get_post_url_via_menu(self, page, post_element) -> str | None:
        try:
            menu_button = await post_element.query_selector("button[aria-label*='Open control menu']")
            if menu_button is None:
                return None
            await menu_button.scroll_into_view_if_needed()
            await menu_button.click()

            copy_item = page.get_by_text("Copy link to post", exact=False)
            await copy_item.first.wait_for(timeout=4000)
            await copy_item.first.click()

            await asyncio.sleep(0.4)
            url = await page.evaluate("() => navigator.clipboard.readText()")
            if url and "linkedin.com" in url:
                return url.strip()
            return None
        except Exception:  # noqa: BLE001
            try:
                await page.keyboard.press("Escape")
            except Exception:  # noqa: BLE001
                pass
            return None

    async def _capture_debug_state(self, page, keyword: str) -> None:
        try:
            out_dir = config.base_dir / "scrape-debug"
            out_dir.mkdir(parents=True, exist_ok=True)
            safe = "".join(c if c.isalnum() else "_" for c in keyword)
            await page.screenshot(path=str(out_dir / f"{safe}.png"), full_page=True)
            html = await page.content()
            (out_dir / f"{safe}.html").write_text(html, encoding="utf-8")
            logger.info("Saved debug capture for '%s' (url: %s) to %s", keyword, page.url, out_dir)
        except Exception as ex:  # noqa: BLE001
            logger.warning("Failed to capture debug state for keyword %s: %s", keyword, ex)


def _url_encode(value: str) -> str:
    from urllib.parse import quote

    return quote(value, safe="")


def _rand_delay() -> float:
    # Random human-like delay between searches (3-7s). Vary without Math.random parity concerns.
    import random

    return random.uniform(3.0, 7.0)
