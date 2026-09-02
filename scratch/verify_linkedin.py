import json
from pathlib import Path
from playwright.sync_api import sync_playwright

cfg = json.loads(Path(r'c:/Users/chait/OneDrive/Desktop/MyProjects/AutoMail/appsettings.local.json').read_text())
email = cfg['LinkedIn']['Email']
password = cfg['LinkedIn']['Password']

print('EMAIL_SET', bool(email), 'PASSWORD_SET', bool(password))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    context = browser.new_context(
        viewport={'width': 1440, 'height': 1200},
        user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
        permissions=['clipboard-read', 'clipboard-write']
    )
    page = context.new_page()
    page.add_init_script("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})")
    page.goto('https://www.linkedin.com/login', timeout=60000)
    print('LOGIN_PAGE_URL', page.url)

    selectors = [
        'input#username', 'input#session_key', 'input[name="session_key"]',
        'input[type="email"]', 'input[autocomplete="username"]'
    ]
    found = False
    for sel in selectors:
        try:
            if page.locator(sel).count() > 0:
                print('EMAIL_SELECTOR_FOUND', sel)
                page.fill(sel, email)
                found = True
                break
        except Exception as e:
            print('EMAIL_SELECTOR_ERR', sel, str(e))
    if not found:
        print('EMAIL_SELECTOR_NOT_FOUND')

    pass_selectors = [
        'input#password', 'input#session_password', 'input[name="session_password"]',
        'input[type="password"]', 'input[autocomplete="current-password"]'
    ]
    pass_found = False
    for sel in pass_selectors:
        try:
            if page.locator(sel).count() > 0:
                print('PASS_SELECTOR_FOUND', sel)
                page.fill(sel, password)
                pass_found = True
                break
        except Exception as e:
            print('PASS_SELECTOR_ERR', sel, str(e))
    if pass_found:
        try:
            after = page.locator('input[type="password"]').first
            after.press('Enter')
        except Exception as e:
            print('PRESS_ERR', str(e))

    try:
        page.wait_for_url('**/feed**', timeout=60000)
        print('LOGIN_OK', page.url)
    except Exception as e:
        print('LOGIN_TIMEOUT', page.url)
        print('LOGIN_ERROR', str(e))
        print('TITLE', page.title())
        try:
            print('BODY_TEXT', page.locator('body').inner_text()[:1500])
        except Exception:
            print('BODY_TEXT_UNAVAILABLE')

    q = 'dotnet developer'
    page.goto(f'https://www.linkedin.com/search/results/content/?datePosted=%22past-24h%22&keywords={q}', timeout=60000)
    page.wait_for_load_state('domcontentloaded')
    print('SEARCH_URL', page.url)

    selector_list = [
        "div[role='listitem']",
        "div[role='listitem']:has([data-testid='expandable-text-box'])",
        "li[role='listitem']",
        "article",
        "div[data-id]",
        "a[href*='/posts/']",
        "a[href*='linkedin.com']"
    ]
    for sel in selector_list:
        try:
            count = page.locator(sel).count()
            print('SEL', sel, 'COUNT', count)
            if count > 0:
                sample = page.locator(sel).first
                try:
                    txt = sample.inner_text()[:300].replace('\n', ' | ')
                except Exception:
                    txt = ''
                print('SAMPLE', txt)
                try:
                    href = sample.locator('a').first.get_attribute('href')
                except Exception:
                    href = ''
                print('HREF', href)
        except Exception as e:
            print('SEL_ERROR', sel, str(e))

    html = page.content()
    print('HTML_HEAD', html[:2000])
    browser.close()
