"""DirectApplyService — auto-apply on Naukri / Indeed via Playwright (port of DirectApplyService.cs)."""
from __future__ import annotations

import asyncio
import json
import logging
from pathlib import Path

from playwright.async_api import async_playwright

from ..config import config
from ..models import ScoutedJob, UserProfile

logger = logging.getLogger(__name__)

_SESSION_DIR = config.base_dir / "Data" / "Sessions"
_NAUKRI_SESSION_FILE = _SESSION_DIR / "naukri_session.json"


class DirectApplyService:
    async def apply(
        self, job: ScoutedJob, profile: UserProfile, resume_file_path: str | None
    ) -> tuple[bool, str]:
        if job.Board == "Naukri":
            return await self._apply_naukri(job, profile, resume_file_path)
        if job.Board == "Indeed":
            return await self._apply_indeed(job, profile, resume_file_path)
        return False, f"Direct apply not supported for board: {job.Board}"

    # ── Naukri ────────────────────────────────────────────────────────────────
    async def _apply_naukri(self, job, profile, resume_file_path) -> tuple[bool, str]:
        logger.info("Direct Apply -> Naukri: %s", job.LinkedInUrl)
        try:
            async with async_playwright() as pw:
                browser = await pw.chromium.launch(
                    headless=False, args=["--disable-blink-features=AutomationControlled"]
                )
                context = await browser.new_context(
                    user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
                    viewport={"width": 1280, "height": 800},
                )
                if not await self._load_naukri_cookies(context):
                    await browser.close()
                    return False, "Naukri session not found. Please run the Scout first to log in."

                page = await context.new_page()
                await page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")
                await page.goto(job.LinkedInUrl, timeout=20000)
                await page.wait_for_load_state("domcontentloaded")
                await asyncio.sleep(2.5)

                already = await page.query_selector(
                    "button[disabled][class*='applied'], .already-applied, [data-qa='already-applied']"
                )
                if already is not None:
                    await browser.close()
                    return True, "Already applied on Naukri previously."

                apply_btn = await self._find_apply_button(page)
                if apply_btn is None:
                    await browser.close()
                    return False, "Could not find Apply button on Naukri job page."

                await apply_btn.click()
                await asyncio.sleep(2)

                if "naukri.com" not in page.url:
                    url = page.url
                    await browser.close()
                    return False, f"Job redirects to external site: {url} — cannot auto-apply."

                result = await self._fill_naukri_form(page, profile, resume_file_path)
                await browser.close()
                return result
        except Exception as ex:  # noqa: BLE001
            logger.error("Naukri direct apply failed for %s: %s", job.LinkedInUrl, ex)
            return False, str(ex)

    async def _find_apply_button(self, page):
        selectors = [
            "button.apply-button",
            "button[data-qa='apply-button']",
            "a.apply-button",
            "button:has-text('Apply')",
            "button:has-text('Apply Now')",
            "[class*='applyBtn']",
            ".nI-gNb-heroSection__applyBtn",
            ".styles_apply-button__N0iSF",
        ]
        for sel in selectors:
            try:
                el = await page.query_selector(sel)
                if el is not None and await el.is_visible():
                    return el
            except Exception:  # noqa: BLE001
                pass
        return None

    async def _fill_naukri_form(self, page, profile, resume_file_path) -> tuple[bool, str]:
        try:
            await asyncio.sleep(1.5)
            if resume_file_path and Path(resume_file_path).exists():
                file_input = await page.query_selector("input[type='file']")
                if file_input is not None:
                    await file_input.set_input_files(resume_file_path)
                    logger.info("Resume uploaded on Naukri apply form.")
                    await asyncio.sleep(1)

            await self._try_fill(page, "input[name='name'], input[placeholder*='Name'], input[id*='name']", profile.FullName)
            await self._try_fill(page, "input[name='email'], input[type='email'], input[placeholder*='Email']", profile.Email)
            await self._try_fill(
                page,
                "input[name='mobile'], input[name='phone'], input[type='tel'], input[placeholder*='Mobile'], input[placeholder*='Phone']",
                profile.Phone or "",
            )

            submit_selectors = [
                "button[type='submit']",
                "button:has-text('Submit')",
                "button:has-text('Apply')",
                "button:has-text('Send Application')",
                "[data-qa='submit-btn']",
            ]
            for sel in submit_selectors:
                try:
                    btn = await page.query_selector(sel)
                    if btn is not None and await btn.is_visible() and await btn.is_enabled():
                        await btn.click()
                        await asyncio.sleep(2)
                        logger.info("Naukri application submitted.")
                        return True, "Application submitted on Naukri."
                except Exception:  # noqa: BLE001
                    pass
            return False, "Could not find submit button on Naukri apply form."
        except Exception as ex:  # noqa: BLE001
            return False, f"Form fill failed: {ex}"

    @staticmethod
    async def _try_fill(page, selector: str, value: str) -> None:
        if not value or not value.strip():
            return
        try:
            el = await page.query_selector(selector)
            if el is not None and await el.is_visible():
                existing = await el.input_value()
                if not existing or not existing.strip():
                    await el.fill(value)
        except Exception:  # noqa: BLE001
            pass

    async def _load_naukri_cookies(self, context) -> bool:
        if not _NAUKRI_SESSION_FILE.exists():
            return False
        try:
            raw = json.loads(_NAUKRI_SESSION_FILE.read_text(encoding="utf-8"))
            cookies = [
                {
                    "name": c.get("name", ""),
                    "value": c.get("value", ""),
                    "domain": c.get("domain", ".naukri.com"),
                    "path": c.get("path", "/"),
                    "httpOnly": bool(c.get("httpOnly", False)),
                    "secure": bool(c.get("secure", False)),
                }
                for c in raw
            ]
            await context.add_cookies(cookies)
            return True
        except Exception:  # noqa: BLE001
            return False

    # ── Indeed ──────────────────────────────────────────────────────────────
    async def _apply_indeed(self, job, profile, resume_file_path) -> tuple[bool, str]:
        logger.info("Direct Apply -> Indeed: %s", job.LinkedInUrl)
        try:
            async with async_playwright() as pw:
                browser = await pw.chromium.launch(
                    headless=False, args=["--disable-blink-features=AutomationControlled"]
                )
                context = await browser.new_context(
                    user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
                    viewport={"width": 1280, "height": 800},
                )
                page = await context.new_page()
                await page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")
                await page.goto(job.LinkedInUrl, timeout=20000)
                await page.wait_for_load_state("domcontentloaded")
                await asyncio.sleep(2.5)

                easy_apply = await page.query_selector(
                    "button[data-testid='indeedApplyButton'], button:has-text('Apply now'), .ia-IndeedApply-modal-trigger"
                )
                if easy_apply is None:
                    external = await page.query_selector(
                        "button:has-text('Apply on company site'), a[target='_blank']"
                    )
                    await browser.close()
                    if external is not None:
                        return False, "Job requires applying on company website — cannot auto-apply."
                    return False, "No Easy Apply button found on Indeed job page."

                await easy_apply.click()
                await asyncio.sleep(2)
                result = await self._fill_indeed_form(page, profile, resume_file_path)
                await browser.close()
                return result
        except Exception as ex:  # noqa: BLE001
            logger.error("Indeed direct apply failed for %s: %s", job.LinkedInUrl, ex)
            return False, str(ex)

    async def _fill_indeed_form(self, page, profile, resume_file_path) -> tuple[bool, str]:
        try:
            for _ in range(5):
                await asyncio.sleep(1.5)
                if resume_file_path and Path(resume_file_path).exists():
                    file_input = await page.query_selector(
                        "input[type='file'][name*='resume'], input[type='file'][accept*='pdf']"
                    )
                    if file_input is not None:
                        await file_input.set_input_files(resume_file_path)
                        await asyncio.sleep(1)

                await self._try_fill(
                    page, "input[name='applicant.name'], input[id*='applicant-name']", profile.FullName
                )
                await self._try_fill(
                    page, "input[name='applicant.phoneNumber'], input[id*='phone']", profile.Phone or ""
                )

                next_btn = await page.query_selector(
                    "button[data-testid='continue-button'], button:has-text('Continue'), "
                    "button:has-text('Next'), button:has-text('Submit your application')"
                )
                if next_btn is None:
                    break
                btn_text = (await next_btn.inner_text()).strip()
                await next_btn.click()
                await asyncio.sleep(2)
                if "submit" in btn_text.lower():
                    logger.info("Indeed Easy Apply submitted.")
                    return True, "Application submitted on Indeed Easy Apply."
            return False, "Could not complete Indeed Easy Apply form — may require manual input."
        except Exception as ex:  # noqa: BLE001
            return False, f"Indeed form fill failed: {ex}"
