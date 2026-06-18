"""
LinkedIn Post Link Scraper
==========================
Logs into LinkedIn, searches for posts using configured keywords (past 24h),
collects post permalink URLs via the "Copy link to post" menu trick,
and saves them to a text file.

Mirrors the logic from the .NET LinkedInScraperService.cs.
"""

import json
import os
import sys
import time
import random
import logging
from datetime import datetime
from urllib.parse import quote
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


# ─── Permalink extraction ────────────────────────────────────────────────────
def get_post_url_via_menu(page, post_element) -> str | None:
    """
    Click the post's "..." menu → "Copy link to post" → read clipboard.
    Returns the LinkedIn URL or None.
    """
    try:
        menu_btn = post_element.query_selector("button[aria-label*='Open control menu']")
        if not menu_btn:
            return None

        menu_btn.scroll_into_view_if_needed()
        menu_btn.click()

        # The menu renders in a portal at page level, so use page-level locator.
        copy_item = page.get_by_text("Copy link to post", exact=False)
        copy_item.first.wait_for(timeout=4000)
        copy_item.first.click()

        # Small delay for the clipboard write
        time.sleep(0.4)
        url = page.evaluate("() => navigator.clipboard.readText()")

        if url and "linkedin.com" in url:
            return url.strip()
        return None

    except Exception:
        # Dismiss any open menu before moving to next post
        try:
            page.keyboard.press("Escape")
        except Exception:
            pass
        return None


# ─── URL helpers ──────────────────────────────────────────────────────────────
def _is_logged_in(url: str) -> bool:
    """Check if the current URL indicates a successful login (feed, home, etc.)."""
    # After login LinkedIn may land on /feed, /home, / (root), or /mynetwork etc.
    # The key is: we're NOT on /login or /checkpoint anymore.
    from urllib.parse import urlparse
    path = urlparse(url).path.rstrip("/")
    login_paths = {"/login", "/uas/login", "/checkpoint/challenge", "/checkpoint/lg/login-submit"}
    # If we're on the root or any non-login path, we're in.
    if path in login_paths:
        return False
    # Also check for common logged-in indicators
    logged_in_indicators = ["feed", "home", "mynetwork", "jobs", "messaging", "notifications", "search"]
    if path == "" or any(ind in url for ind in logged_in_indicators):
        return True
    # If it's a linkedin.com page that's NOT login/checkpoint, assume logged in
    return "linkedin.com" in url and "/login" not in url and "/checkpoint" not in url


# ─── Main scraper ─────────────────────────────────────────────────────────────
def run_scraper():
    config = load_config()

    email = config["linkedin_email"]
    password = config["linkedin_password"]
    keywords = config["keywords"]
    max_posts = config.get("max_posts_per_keyword", 300)
    output_file = config.get("output_file", "../scraped_links.txt")
    headless = config.get("headless", False)
    max_scrolls = config.get("max_scrolls", 30)
    scroll_delay = config.get("scroll_delay_ms", 2000) / 1000.0
    delay_range = config.get("search_delay_range", [3000, 7000])

    # Resolve output path relative to this script's directory
    output_path = Path(__file__).parent / output_file
    output_path = output_path.resolve()

    log.info("Output file: %s", output_path)
    log.info("Keywords: %s", keywords)
    log.info("Max posts/keyword: %d | Headless: %s", max_posts, headless)

    # Collect results: { keyword: [url, ...] }
    results: dict[str, list[str]] = {}

    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=headless)
        context = browser.new_context(
            user_agent=(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/120.0.0.0 Safari/537.36"
            ),
            viewport={"width": 1280, "height": 720},
            permissions=["clipboard-read", "clipboard-write"],
        )
        page = context.new_page()

        # Bypass basic WebDriver detection
        page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")

        # ── Login ─────────────────────────────────────────────────────────
        log.info("Logging into LinkedIn...")
        page.goto("https://www.linkedin.com/login", wait_until="domcontentloaded")
        log.info("Login page loaded. URL: %s", page.url)
        time.sleep(3)

        # Check if already logged in (session cookie from previous run)
        log.info("Post-load URL: %s", page.url)
        if not _is_logged_in(page.url) and "checkpoint" not in page.url:
            log.info("Not logged in yet — filling credentials...")
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
                log.info("Email filled.")
                page.fill(pass_sel, password)
                log.info("Password filled. Submitting...")

                # Click the sign-in button instead of pressing Enter (more reliable)
                sign_in_btn = page.query_selector("button[type='submit']:visible, button[data-litms-control-urn*='login-submit']:visible")
                if sign_in_btn:
                    sign_in_btn.click()
                    log.info("Clicked Sign In button.")
                else:
                    page.press(pass_sel, "Enter")
                    log.info("Pressed Enter on password field.")

                # Wait for navigation after login submit
                try:
                    page.wait_for_url(lambda url: "/login" not in url, timeout=30000)
                    log.info("Navigated away from login. URL: %s", page.url)
                except PlaywrightTimeout:
                    log.warning("URL didn't change from /login within 30s. URL: %s", page.url)
            except PlaywrightTimeout:
                log.error("Could not find login form fields. Taking screenshot...")
                page.screenshot(path=str(Path(__file__).parent / "login_error.png"))
                browser.close()
                sys.exit(1)

        # Wait for feed/home or checkpoint — also check for feed elements in the DOM
        log.info("Waiting for feed to load... Current URL: %s", page.url)
        feed_loaded = False
        for i in range(60):
            current_url = page.url
            if _is_logged_in(current_url) or "checkpoint" in current_url:
                feed_loaded = True
                log.info("Detected logged-in state at URL: %s (after %ds)", current_url, i)
                break
            # Fallback: check if feed elements exist in DOM even if URL is weird
            if i > 5:
                has_feed = page.query_selector("div.feed-shared-update-v2, [data-testid='home-feed'], .scaffold-layout, .global-nav")
                if has_feed:
                    feed_loaded = True
                    log.info("Detected feed elements in DOM despite URL: %s (after %ds)", current_url, i)
                    break
            if i % 10 == 0:
                log.info("  Still waiting... URL: %s (%ds elapsed)", current_url, i)
            time.sleep(1)

        if not feed_loaded:
            log.error("Failed to reach feed/checkpoint after 60s. URL: %s", page.url)
            try:
                page.screenshot(path=str(Path(__file__).parent / "login_error.png"))
                log.info("Screenshot saved to login_error.png")
            except Exception:
                pass
            browser.close()
            sys.exit(1)

        # Handle CAPTCHA / security checkpoint
        if "checkpoint" in page.url and not _is_logged_in(page.url):
            log.warning(
                "⚠️  Security checkpoint detected! "
                "Please solve the CAPTCHA in the browser window..."
            )
            for _ in range(90):
                if _is_logged_in(page.url):
                    log.info("✅ Checkpoint cleared!")
                    break
                time.sleep(1)
            else:
                log.error("Checkpoint not cleared within 90s. Exiting.")
                browser.close()
                sys.exit(1)

        time.sleep(5)
        log.info("✅ Login successful — feed loaded. URL: %s", page.url)

        # ── Search each keyword ───────────────────────────────────────────
        for keyword in keywords:
            log.info("━" * 60)
            log.info("Searching for: '%s'", keyword)
            encoded = quote(keyword)
            search_url = (
                f"https://www.linkedin.com/search/results/content/"
                f"?datePosted=%22past-24h%22&keywords={encoded}"
            )
            page.goto(search_url)
            page.wait_for_load_state("domcontentloaded")

            post_selector = "div[role='listitem']:has([data-testid='expandable-text-box'])"

            try:
                page.wait_for_selector(post_selector, timeout=15000)
            except PlaywrightTimeout:
                pass  # Fall through to check if any results loaded

            post_elements = page.query_selector_all(post_selector)
            log.info("Initially found %d posts for '%s'", len(post_elements), keyword)

            if not post_elements:
                capture_debug_state(page, keyword)
                log.warning(
                    "No results for '%s'. Debug screenshot saved. "
                    "Common causes: selector changes, activity wall, or empty results.",
                    keyword,
                )
                results[keyword] = []
                continue

            # ── Scroll & collect ──────────────────────────────────────────
            seen_keys: set[str] = set()
            collected_urls: list[str] = []
            empty_rounds = 0

            for scroll in range(max_scrolls + 1):
                elements = page.query_selector_all(post_selector)
                new_this_round = 0

                for element in elements:
                    try:
                        text_el = element.query_selector("[data-testid='expandable-text-box']")
                        raw_text = text_el.inner_text() if text_el else ""
                        if not raw_text.strip():
                            continue

                        # Dedup key (first 200 chars of text)
                        key = raw_text.strip()[:200]
                        if key in seen_keys:
                            continue
                        seen_keys.add(key)

                        # Get permalink via menu
                        url = get_post_url_via_menu(page, element)
                        if not url:
                            log.debug("Could not get permalink for a '%s' post, skipping.", keyword)
                            continue

                        collected_urls.append(url)
                        new_this_round += 1

                    except Exception as e:
                        log.warning("Failed to extract a post for '%s': %s", keyword, e)

                log.info(
                    "  Keyword '%s' — scroll %d/%d: +%d new, %d total",
                    keyword, scroll, max_scrolls, new_this_round, len(collected_urls),
                )

                if len(collected_urls) >= max_posts:
                    break

                if new_this_round == 0:
                    empty_rounds += 1
                    if empty_rounds >= 4:
                        log.info(
                            "  No new posts after %d consecutive scrolls — stopping for '%s'.",
                            empty_rounds, keyword,
                        )
                        break
                else:
                    empty_rounds = 0

                page.evaluate("window.scrollBy(0, 1400)")
                time.sleep(scroll_delay)

            log.info("Finished '%s' — %d URLs collected.", keyword, len(collected_urls))
            results[keyword] = collected_urls

            # Human-like delay between keyword searches
            delay = random.randint(delay_range[0], delay_range[1]) / 1000.0
            time.sleep(delay)

        browser.close()

    # ── Write output file ─────────────────────────────────────────────────
    total = sum(len(urls) for urls in results.values())
    log.info("━" * 60)
    log.info("Writing %d total URLs to %s", total, output_path)

    # Deduplicate across all keywords
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

    # Rewrite the header with actual count
    content = output_path.read_text(encoding="utf-8")
    content = content.replace("{PLACEHOLDER}", str(unique_count))
    output_path.write_text(content, encoding="utf-8")

    log.info("✅ Done! %d unique links saved to %s", unique_count, output_path)

    # Summary
    log.info("━" * 60)
    log.info("SUMMARY")
    for keyword, urls in results.items():
        log.info("  %-40s  %d links", keyword, len(urls))
    log.info("  %-40s  %d links", "TOTAL (deduplicated)", unique_count)


if __name__ == "__main__":
    run_scraper()
