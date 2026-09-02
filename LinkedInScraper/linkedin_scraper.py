"""
LinkedIn Post Link Scraper
==========================
Logs into LinkedIn, searches for posts using configured keywords (past 24h),
collects post permalink URLs, and saves them to a text file.

Mirrors the logic from the .NET LinkedInScraperService.cs.
"""

import json
import os
import sys
import time
import random
import re
import logging
from datetime import datetime
from urllib.parse import quote, urlparse
from pathlib import Path

from playwright.sync_api import sync_playwright, TimeoutError as PlaywrightTimeout

# ─── Logging ──────────────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("linkedin_scraper")


# ─── Config ───────────────────────────────────────────────────────────────────
def load_config() -> dict:
    """Load config.json from the same directory as this script."""
    config_path = Path(__file__).parent / "config.json"
    if not config_path.exists():
        log.error("config.json not found at %s", config_path)
        sys.exit(1)
    with open(config_path, "r", encoding="utf-8") as f:
        return json.load(f)


# ─── Debug capture ────────────────────────────────────────────────────────────
def capture_debug_state(page, keyword: str):
    """Save screenshot + HTML when 0 results found, for debugging selectors."""
    debug_dir = Path(__file__).parent / "scrape-debug"
    debug_dir.mkdir(exist_ok=True)
    safe_name = "".join(c if c.isalnum() else "_" for c in keyword)
    try:
        page.screenshot(path=str(debug_dir / f"{safe_name}.png"), full_page=True)
        html = page.content()
        (debug_dir / f"{safe_name}.html").write_text(html, encoding="utf-8")
        log.info("Saved debug capture for '%s' to %s", keyword, debug_dir)
    except Exception as e:
        log.warning("Failed to capture debug state for '%s': %s", keyword, e)


# ─── Post URL Helpers ────────────────────────────────────────────────────────
def is_valid_post_url(url: str) -> bool:
    if not url:
        return False
    u = url.lower()
    if "/in/" in u and "activity" not in u:
        return False
    if "/company/" in u or "/school/" in u:
        return False
    return (
        "/feed/update/" in u
        or "/posts/" in u
        or "urn:li:activity" in u
        or "urn:li:share" in u
        or "urn:li:ugcpost" in u
        or "highlightedupdateurn" in u
    )


def clean_post_url(href: str) -> str:
    if href.startswith("/"):
        href = "https://www.linkedin.com" + href
    if "?" in href and "highlightedUpdateUrn" not in href:
        href = href.split("?")[0]
    return href.strip()


def resolve_post_url(page, post_element) -> str | None:
    """Extract post URL via direct anchor, URN attribute, or 3-dots menu."""
    # 1. Direct post permalink anchors
    selectors = [
        "a[href*='/feed/update/urn:li:activity:']",
        "a[href*='/feed/update/urn:li:share:']",
        "a[href*='/feed/update/urn:li:ugcPost:']",
        "a[href*='/feed/update/']",
        "a[href*='/posts/']",
        "a.update-components-actor__sub-description-link",
        "a.feed-shared-actor__sub-description-link",
        "a[href*='activity-']",
        "a[href*='urn:li:activity']",
    ]
    for sel in selectors:
        try:
            anchor = post_element.query_selector(sel)
            if anchor:
                href = anchor.get_attribute("href")
                if href and is_valid_post_url(href):
                    return clean_post_url(href)
        except Exception:
            pass

    # 2. URN attributes
    for attr in ["data-urn", "data-id", "data-chameleon-result-urn", "data-activity-urn"]:
        try:
            val = post_element.get_attribute(attr)
            if val:
                match = re.search(r"urn:li:(activity|share|ugcPost):(\d+)", val)
                if match:
                    return f"https://www.linkedin.com/feed/update/{match.group(0)}/"
        except Exception:
            pass

    # 3. Via 3-dots menu
    return get_post_url_via_menu(page, post_element)


def get_post_url_via_menu(page, post_element) -> str | None:
    """Click post's '...' menu → 'Copy link to post' → read clipboard."""
    menu_selectors = [
        "button[aria-label*='Open control menu']",
        "button[aria-label*='control menu']",
        "button[aria-label*='More actions']",
        "button[aria-label*='More options']",
        "button[aria-label*='options for this update']",
        "button.feed-shared-control-menu__trigger",
        "button.artdeco-dropdown__trigger",
        "div.feed-shared-control-menu button",
    ]
    try:
        menu_btn = None
        for sel in menu_selectors:
            menu_btn = post_element.query_selector(sel)
            if menu_btn:
                break

        if not menu_btn:
            return None

        menu_btn.scroll_into_view_if_needed()
        menu_btn.click()
        time.sleep(0.4)

        copy_item = page.locator("text='Copy link to post', text='Copy link'").first
        copy_item.wait_for(timeout=3000)
        copy_item.click()

        time.sleep(0.5)
        url = page.evaluate("() => navigator.clipboard.readText()")

        try:
            page.keyboard.press("Escape")
        except Exception:
            pass

        if url and is_valid_post_url(url):
            return clean_post_url(url)
        return None

    except Exception:
        try:
            page.keyboard.press("Escape")
        except Exception:
            pass
        return None


# ─── URL helpers ──────────────────────────────────────────────────────────────
def _is_logged_in(url: str) -> bool:
    """Check if current URL indicates a successful login."""
    path = urlparse(url).path.rstrip("/")
    login_paths = {"/login", "/uas/login", "/checkpoint/challenge", "/checkpoint/lg/login-submit"}
    if path in login_paths:
        return False
    logged_in_indicators = ["feed", "home", "mynetwork", "jobs", "messaging", "notifications", "search"]
    if path == "" or any(ind in url for ind in logged_in_indicators):
        return True
    return "linkedin.com" in url and "/login" not in url and "/checkpoint" not in url


def _ensure_logged_in(page, email: str, password: str):
    log.info("Checking LinkedIn login state...")
    page.goto("https://www.linkedin.com/feed/", wait_until="load")
    time.sleep(3)

    if _is_logged_in(page.url):
        has_feed = page.query_selector(
            ".global-nav, .scaffold-layout, [data-testid='home-feed'], div.feed-shared-update-v2"
        )
        if has_feed:
            log.info("✅ Already logged in from persistent session. URL: %s", page.url)
            return

    log.info("Logging into LinkedIn...")
    page.goto("https://www.linkedin.com/login", wait_until="domcontentloaded")
    time.sleep(3)

    if _is_logged_in(page.url):
        log.info("✅ Auto-redirected to logged-in state. URL: %s", page.url)
        return

    if "checkpoint" not in page.url:
        log.info("Filling credentials...")
        email_sel = (
            "input#username:visible, input#session_key:visible, "
            "input[name='session_key']:visible, input[type='email']:visible, "
            "input[autocomplete='username']:visible"
        )
        pass_sel = (
            "input#password:visible, input#session_password:visible, "
            "input[name='session_password']:visible, input[type='password']:visible, "
            "input[autocomplete='current-password']:visible"
        )

        try:
            page.wait_for_selector(email_sel, timeout=10000)
            page.fill(email_sel, email)
            page.fill(pass_sel, password)

            sign_in_btn = page.query_selector("button[type='submit']:visible, button[data-litms-control-urn*='login-submit']:visible")
            if sign_in_btn:
                sign_in_btn.click()
            else:
                page.press(pass_sel, "Enter")

            try:
                page.wait_for_url(lambda url: "/login" not in url, timeout=30000)
            except PlaywrightTimeout:
                pass
        except PlaywrightTimeout:
            log.error("Could not find login form fields.")
            page.screenshot(path=str(Path(__file__).parent / "login_error.png"))
            raise

    feed_loaded = False
    for i in range(60):
        current_url = page.url
        if _is_logged_in(current_url) or "checkpoint" in current_url:
            feed_loaded = True
            break
        if i > 5:
            has_feed = page.query_selector("div.feed-shared-update-v2, [data-testid='home-feed'], .scaffold-layout, .global-nav")
            if has_feed:
                feed_loaded = True
                break
        time.sleep(1)

    if not feed_loaded:
        log.error("Failed to reach feed/checkpoint after 60s. URL: %s", page.url)
        page.screenshot(path=str(Path(__file__).parent / "login_error.png"))
        raise RuntimeError(f"Failed to reach LinkedIn feed after login. URL: {page.url}")

    if "checkpoint" in page.url and not _is_logged_in(page.url):
        log.warning("⚠️ Security checkpoint detected! Please solve verification in browser window...")
        for _ in range(120):
            if _is_logged_in(page.url):
                log.info("✅ Checkpoint cleared!")
                break
            time.sleep(1)
        else:
            log.error("Checkpoint not cleared within 120s.")
            raise RuntimeError("LinkedIn checkpoint not cleared in time.")

    time.sleep(4)
    log.info("✅ Login successful — feed loaded. URL: %s", page.url)


# ─── Main scraper ─────────────────────────────────────────────────────────────
def run_scraper():
    config = load_config()

    email = config["linkedin_email"]
    password = config["linkedin_password"]
    keywords = config["keywords"]
    max_posts = config.get("max_posts_per_keyword", 300)
    output_file = config.get("output_file", "../scraped_links.txt")
    headless = config.get("headless", False)
    max_scrolls = config.get("max_scrolls", 25)
    delay_range = config.get("search_delay_range", [4000, 8000])

    output_path = (Path(__file__).parent / output_file).resolve()
    session_dir = str(Path(__file__).parent / ".linkedin-session")
    os.makedirs(session_dir, exist_ok=True)

    log.info("Output file: %s", output_path)
    log.info("Keywords: %s", keywords)

    results: dict[str, list[str]] = {}

    with sync_playwright() as pw:
        context = pw.chromium.launch_persistent_context(
            session_dir,
            headless=headless,
            user_agent=(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/126.0.0.0 Safari/537.36"
            ),
            viewport={"width": 1280, "height": 720},
            permissions=["clipboard-read", "clipboard-write"],
            args=["--disable-blink-features=AutomationControlled"],
        )

        page = context.pages[0] if context.pages else context.new_page()
        page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")

        try:
            _ensure_logged_in(page, email, password)

            for keyword in keywords:
                log.info("━" * 60)
                log.info("Searching for: '%s'", keyword)
                encoded = quote(keyword)
                search_url = (
                    f"https://www.linkedin.com/search/results/content/"
                    f"?datePosted=%22past-24h%22&sortBy=%22date_posted%22&keywords={encoded}"
                )
                page.goto(search_url, wait_until="load")
                time.sleep(4)

                if not _is_logged_in(page.url):
                    log.warning("LinkedIn session expired — re-logging in...")
                    _ensure_logged_in(page, email, password)
                    page.goto(search_url, wait_until="load")
                    time.sleep(4)

                    if not _is_logged_in(page.url):
                        log.error("Could not re-establish session. Skipping keyword '%s'.", keyword)
                        capture_debug_state(page, keyword)
                        results[keyword] = []
                        continue

                post_selector = "div[role='listitem']:has([data-testid='expandable-text-box']), div.feed-shared-update-v2, div[role='listitem']"

                try:
                    page.wait_for_selector(post_selector, timeout=15000)
                except PlaywrightTimeout:
                    pass

                post_elements = page.query_selector_all(post_selector)
                log.info("Initially found %d post candidates for '%s'", len(post_elements), keyword)

                if not post_elements:
                    capture_debug_state(page, keyword)
                    results[keyword] = []
                    continue

                seen_keys: set[str] = set()
                collected_urls: list[str] = []
                empty_rounds = 0

                for scroll in range(max_scrolls + 1):
                    elements = page.query_selector_all(post_selector)
                    new_this_round = 0

                    for element in elements:
                        try:
                            text_el = element.query_selector("[data-testid='expandable-text-box'], .feed-shared-text, div[class*='text-body']")
                            raw_text = text_el.inner_text() if text_el else ""
                            if not raw_text.strip() or len(raw_text.strip()) < 20:
                                continue

                            key = raw_text.strip()[:200]
                            if key in seen_keys:
                                continue
                            seen_keys.add(key)

                            url = resolve_post_url(page, element)
                            if not url or url in collected_urls:
                                continue

                            collected_urls.append(url)
                            new_this_round += 1

                        except Exception as e:
                            log.warning("Failed to extract post: %s", e)

                    log.info(
                        "  Keyword '%s' — scroll %d/%d: +%d new, %d total",
                        keyword, scroll, max_scrolls, new_this_round, len(collected_urls),
                    )

                    if len(collected_urls) >= max_posts:
                        break

                    if new_this_round == 0:
                        empty_rounds += 1
                        if empty_rounds >= 5:
                            log.info("  No new posts after %d consecutive scrolls.", empty_rounds)
                            break
                    else:
                        empty_rounds = 0

                    try:
                        if elements:
                            elements[-1].scroll_into_view_if_needed()
                    except Exception:
                        pass

                    page.evaluate("window.scrollTo(0, document.body.scrollHeight)")
                    page.keyboard.press("PageDown")
                    time.sleep(1)
                    page.keyboard.press("PageDown")
                    time.sleep(2)

                    try:
                        show_more = page.query_selector("button:has-text('Show more results'), button:has-text('See more posts')")
                        if show_more and show_more.is_visible():
                            show_more.click()
                            time.sleep(2)
                    except Exception:
                        pass

                log.info("Finished '%s' — %d valid post URLs collected.", keyword, len(collected_urls))
                results[keyword] = collected_urls

                delay = random.randint(delay_range[0], delay_range[1]) / 1000.0
                time.sleep(delay)

        finally:
            context.close()

    total = sum(len(urls) for urls in results.values())
    log.info("━" * 60)
    log.info("Writing %d total URLs to %s", total, output_path)

    all_urls_seen: set[str] = set()
    with open(output_path, "w", encoding="utf-8") as f:
        f.write(f"# LinkedIn Post Links — Scraped {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"# Total unique links: {{PLACEHOLDER}}\n\n")

        unique_count = 0
        for keyword, urls in results.items():
            f.write(f"## [{keyword}]\n")
            keyword_count = 0
            for url in urls:
                if url not in all_urls_seen:
                    all_urls_seen.add(url)
                    f.write(f"{url}\n")
                    keyword_count += 1
                    unique_count += 1
            f.write(f"# {keyword_count} links for this keyword\n\n")

    content = output_path.read_text(encoding="utf-8")
    content = content.replace("{PLACEHOLDER}", str(unique_count))
    output_path.write_text(content, encoding="utf-8")

    log.info("✅ Done! %d unique post links saved to %s", unique_count, output_path)


if __name__ == "__main__":
    run_scraper()
