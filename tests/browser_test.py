"""Headless-browser check of the web button, against the server tests/integration.sh leaves running.

  docker run --rm --network host -v "$PWD/tests:/t" -v /tmp/am-test:/am:ro \
    mcr.microsoft.com/playwright/python:v1.63.0-noble python /t/browser_test.py

Logs in through the API with the integration test's throwaway accounts, then drives jellyfin-web.
"""

import hashlib
import json
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:18096"
CLIENT = 'MediaBrowser Client="am-browser", Device="am-browser", DeviceId="am-browser-1", Version="1.0"'
creds = dict(line.split() for line in open("/am/creds"))
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS  " if ok else "FAIL  ") + name + (f"  ({detail})" if detail and not ok else ""))


def api(path, token=None, data=None):  # noqa: D103
    headers = {"Content-Type": "application/json", "Authorization": CLIENT + (f', Token="{token}"' if token else "")}
    req = urllib.request.Request(BASE + path, data=json.dumps(data).encode() if data is not None else None, headers=headers)
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read() or b"null")


def login(user, pw):
    r = api("/Users/AuthenticateByName", data={"Username": user, "Pw": pw})
    return r["AccessToken"], r["User"]["Id"]


def item_id(token, kind, path):
    items = api(f"/Items?Recursive=true&IncludeItemTypes={kind}&Fields=Path", token)["Items"]
    return next(i["Id"] for i in items if i.get("Path") == path)


server = api("/System/Info/Public")
admin_token, admin_id = login("admin", creds["admin"])
reader_token, reader_id = login("reader", creds["users"])
series = item_id(admin_token, "Series", "/media/training/Course A")
season1 = item_id(admin_token, "Season", "/media/training/Course A/Season 1")
lesson1 = item_id(admin_token, "Episode", "/media/training/Course A/Season 1/S01E01 - Lesson One.mp4")
training = next(v["ItemId"] for v in api("/Library/VirtualFolders", admin_token) if v["Name"] == "training")
expected_lesson = hashlib.sha256(open("/am/media/training/Course A/Season 1/S01E01 - Lesson One.material.zip", "rb").read()).hexdigest()
plain = item_id(admin_token, "Episode", "/media/training/Course A/Season 2/S02E01 - Sneaky.mp4")
expected = hashlib.sha256(open("/am/media/training/Course A/additional-material.zip", "rb").read()).hexdigest()


def session(browser, token, user_id):
    ctx = browser.new_context(accept_downloads=True, viewport={"width": 1280, "height": 800})
    stored = {"Servers": [{"ManualAddress": BASE, "LocalAddress": BASE, "Id": server["Id"], "Name": server["ServerName"],
                           "UserId": user_id, "AccessToken": token, "DateLastAccessed": int(time.time() * 1000),
                           "LastConnectionMode": 2, "manualAddressOnly": True}]}
    ctx.add_init_script(f"localStorage.setItem('jellyfin_credentials', {json.dumps(json.dumps(stored))});")
    return ctx, ctx.new_page()


def open_details(page, item):
    page.goto(f"{BASE}/web/#/details?id={item}&serverId={server['Id']}")
    page.wait_for_selector(".itemDetailPage:not(.hide) .mainDetailButtons", timeout=60000)


with sync_playwright() as p:
    browser = p.chromium.launch()

    ctx, page = session(browser, admin_token, admin_id)
    open_details(page, series)
    button = page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton")
    try:
        button.wait_for(timeout=20000)
        check("button appears on a course with material", True)
    except Exception as e:  # noqa: BLE001
        check("button appears on a course with material", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-course.png")
    if button.count():
        check("button is enabled for a user with download permission", button.is_enabled())
        check("course button counts all archives below", "3 archives" in (button.get_attribute("title") or ""), button.get_attribute("title"))
        button.click()
        dialog = page.locator(".am-dialog")
        try:
            dialog.wait_for(timeout=10000)
            check("course button opens the listing", True)
        except Exception as e:  # noqa: BLE001
            check("course button opens the listing", False, str(e)[:120])
        page.screenshot(path="/t/screenshot-listing.png")
        check("listing has course, section and lesson rows", dialog.locator(".am-row").count() == 3, str(dialog.locator(".am-row").count()))
        with page.expect_download(timeout=30000) as d:
            dialog.locator(".am-row").nth(0).locator(".am-download").click()
        check("listing downloads the course archive", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected)
        lesson_row = dialog.locator(".am-row").nth(2)   # names may come from online metadata; the lesson row is last
        with page.expect_download(timeout=30000) as d:
            lesson_row.locator(".am-download").click()
        check("listing downloads a lesson archive", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected_lesson)
        lesson_row.locator(".am-goto").click()
        page.wait_for_timeout(1500)
        check("Go to lesson opens the lesson page", lesson1 in page.url.replace("-", ""), page.url)
        check("listing closes on Go to lesson", page.locator(".am-dialog").count() == 0)
        page.wait_for_selector(".itemDetailPage:not(.hide) .additionalMaterialButton", timeout=20000)
        lb = page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton")
        check("lesson button names format and size", "ZIP" in (lb.get_attribute("title") or ""), lb.get_attribute("title"))
        with page.expect_download(timeout=30000) as d:
            lb.click()
        check("lesson button downloads directly", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected_lesson)
        open_details(page, series)
        page.wait_for_selector(".itemDetailPage:not(.hide) .additionalMaterialButton", timeout=20000)
    if button.count():
        color = page.evaluate("() => getComputedStyle(document.querySelector('.additionalMaterialIcon')).getPropertyValue('--am-accent').trim()")
        check("two-color style puts the accent on the badge", color.lower() == "#db781b", color)
        check("button shows the A6 icon (inline SVG)", page.locator(".additionalMaterialButton svg").count() == 1)
    open_details(page, plain)
    page.wait_for_timeout(3000)
    check("no button on an item without material", page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton").count() == 0)
    open_details(page, series)
    page.wait_for_timeout(3000)
    check("exactly one button after returning to the course", page.locator(".additionalMaterialButton").count() == 1,
          str(page.locator(".additionalMaterialButton").count()))
    # Grid: the training library's series cards
    page.goto(f"{BASE}/web/#/tv?topParentId={training}&serverId={server['Id']}")
    try:
        page.wait_for_selector(".card .additionalMaterialIndicator", timeout=30000)
        check("grid card shows the indicator", True)
        last = page.evaluate("() => { const i = document.querySelector('.additionalMaterialIndicator'); return i.parentElement.classList.contains('cardIndicators') && i === i.parentElement.lastElementChild; }")
        check("indicator is last in the card's top-right row", last)
        before = page.url
        # Click where the pointer is, as a person would: the card's hover overlay covers the indicator,
        # and Playwright's locator.click() refuses covered elements.
        # Jellyfin redraws cards as images load; wait until the indicator is settled and visible.
        page.wait_for_load_state("networkidle")
        box = None
        for _ in range(10):
            ind = page.locator(".card .additionalMaterialIndicator:visible").first
            ind.wait_for(state="visible", timeout=10000)
            page.wait_for_timeout(500)
            box = ind.bounding_box()
            if box:
                break
        page.mouse.move(box["x"] + box["width"] / 2, box["y"] + box["height"] / 2)
        page.wait_for_timeout(300)
        page.mouse.click(box["x"] + box["width"] / 2, box["y"] + box["height"] / 2)
        page.locator(".am-dialog").wait_for(timeout=10000)
        check("clicking the indicator opens the listing, not the item", page.url == before, page.url)
        page.screenshot(path="/t/screenshot-grid.png")
        page.keyboard.press("Escape")
        check("Escape closes the listing", page.locator(".am-dialog").count() == 0)
    except Exception as e:  # noqa: BLE001
        check("grid card shows the indicator", False, str(e)[:120])
        page.screenshot(path="/t/screenshot-grid.png")
    # List: the season page lists its episodes
    open_details(page, season1)
    try:
        page.wait_for_selector(".listItem .additionalMaterialListButton", timeout=30000)
        check("list row shows the icon", True)
        beside = page.evaluate("() => { const b = document.querySelector('.additionalMaterialListButton'); const n = b.nextElementSibling; return !!n && n.getAttribute('is') === 'emby-ratingbutton'; }")
        check("list icon sits beside the favorite heart", beside)
        page.screenshot(path="/t/screenshot-list.png")
    except Exception as e:  # noqa: BLE001
        check("list row shows the icon", False, str(e)[:120])
        page.screenshot(path="/t/screenshot-list.png")
    # Race: Jellyfin re-draws cards or rows while the status request is still on its way. The new
    # elements must still get their icon (1.2.1 marked them "no material" for good). Checked in
    # both places icons appear: grid cards and list rows.
    def redraw_race(view, url, items, icon):
        held, released = [], []
        race = ctx.new_page()

        def hold(route):
            if released:
                route.continue_()
            else:
                held.append(route)

        race.route("**/AdditionalMaterial/Items/Status", hold)
        race.goto(url)
        name = f"{view}: re-drawn mid-request still gets the icon"
        try:
            for _ in range(150):
                if held and race.locator(items).count():
                    break
                race.wait_for_timeout(200)
            race.wait_for_timeout(500)
            race.evaluate("(sel) => document.querySelectorAll(sel).forEach(c => c.replaceWith(c.cloneNode(true)))", items)
            race.wait_for_timeout(500)
            released.append(True)
            for r in held:
                r.continue_()
            race.wait_for_selector(f"{items} {icon}", timeout=15000)
            check(name, True)
        except Exception as e:  # noqa: BLE001
            check(name, False, f"held={len(held)} " + str(e)[:120])
        race.close()

    redraw_race("grid", f"{BASE}/web/#/tv?topParentId={training}&serverId={server['Id']}", ".card[data-id]", ".additionalMaterialIndicator")
    redraw_race("list", f"{BASE}/web/#/details?id={season1}&serverId={server['Id']}", ".listItem[data-id]", ".additionalMaterialListButton")
    page.goto(f"{BASE}/web/#/dashboard")
    try:
        page.get_by_text("Additional Material", exact=True).first.wait_for(timeout=30000)
        check("dashboard sidebar lists the plugin", True)
    except Exception as e:  # noqa: BLE001
        check("dashboard sidebar lists the plugin", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-dashboard.png")
    page.goto(f"{BASE}/web/#/configurationpage?name=Additional%20Material")
    try:
        page.locator("#amPreview svg").wait_for(timeout=30000)
        check("settings page shows the icon preview", True)
        check("settings page lists the libraries", page.locator("#amLibraries input").count() == 2)
    except Exception as e:  # noqa: BLE001
        check("settings page shows the icon preview", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-settings.png", full_page=True)
    ctx.close()

    ctx, page = session(browser, reader_token, reader_id)
    open_details(page, series)
    button = page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton")
    try:
        button.wait_for(timeout=20000)
        button.click()
        page.locator(".am-dialog").wait_for(timeout=10000)
        disabled = page.evaluate("() => [...document.querySelectorAll('.am-download')].every(b => b.disabled)")
        check("no download permission: listing's Download buttons are disabled", disabled)
        check("no download permission: listing says so", page.locator(".am-note").count() >= 1)
    except Exception as e:  # noqa: BLE001
        check("no download permission: listing's Download buttons are disabled", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-no-permission.png")
    open_details(page, lesson1)
    try:
        lb = page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton")
        lb.wait_for(timeout=20000)
        check("no download permission: lesson button is disabled", not lb.is_enabled())
    except Exception as e:  # noqa: BLE001
        check("no download permission: lesson button is disabled", False, str(e)[:120])
    ctx.close()
    browser.close()

print(f"\npassed {sum(results)}, failed {len(results) - sum(results)}")
sys.exit(0 if all(results) else 1)
