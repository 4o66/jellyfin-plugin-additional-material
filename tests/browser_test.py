"""Headless-browser check of the web button, against the server tests/integration.sh leaves running.

  docker run --rm --network host -v "$PWD/tests:/t" -v /tmp/am-test:/am:ro \
    mcr.microsoft.com/playwright/python:v1.63.0-noble \
    sh -c 'pip install -q --break-system-packages playwright==1.63.0 && python /t/browser_test.py'

(The image carries the browsers but, at v1.63.0, not the playwright package itself.)

Logs in through the API with the integration test's throwaway accounts, then drives jellyfin-web.

Against a server tests/integration.py set up (Windows, or a native Linux install), copy that run's
creds and run.json into a folder and mount it as /am instead: the server's address, its media
paths and the archives' hashes are read from run.json, so the server's disk need not be mounted.
AM_BASE overrides the address, for a server run.json names as 127.0.0.1.
"""

import hashlib
import json
import os
import sys
import time
import urllib.request

from playwright.sync_api import sync_playwright

RUN = json.load(open("/am/run.json")) if os.path.exists("/am/run.json") else None
BASE = os.environ.get("AM_BASE") or (RUN or {}).get("base") or "http://127.0.0.1:18096"
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


def spath(rel):
    """A media path as the server sees it."""
    return RUN["media"] + RUN["sep"] + rel.replace("/", RUN["sep"]) if RUN else "/media/" + rel


def digest(key, rel):
    return RUN["sha256"][key] if RUN else hashlib.sha256(open("/am/media/" + rel, "rb").read()).hexdigest()


def item_id(token, kind, path):
    fold = (lambda p: p.lower()) if RUN and RUN["sep"] == "\\" else (lambda p: p)
    items = api(f"/Items?Recursive=true&IncludeItemTypes={kind}&Fields=Path", token)["Items"]
    return next(i["Id"] for i in items if fold(i.get("Path") or "") == fold(path))


server = api("/System/Info/Public")
admin_token, admin_id = login("admin", creds["admin"])
reader_token, reader_id = login("reader", creds["users"])
series = item_id(admin_token, "Series", spath("training/Course A"))
season1 = item_id(admin_token, "Season", spath("training/Course A/Season 1"))
lesson1 = item_id(admin_token, "Episode", spath("training/Course A/Season 1/S01E01 - Lesson One.mp4"))
training = next(v["ItemId"] for v in api("/Library/VirtualFolders", admin_token) if v["Name"] == "training")
expected_lesson = digest("lesson", "training/Course A/Season 1/S01E01 - Lesson One.material.zip")
plain = item_id(admin_token, "Episode", spath("training/Course A/Season 2/S02E01 - Sneaky.mp4"))
expected = digest("course", "training/Course A/additional-material.zip")


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
    MAIN = ".itemDetailPage:not(.hide) .additionalMaterialButton:not(.additionalMaterialContents)"
    CONTENTS = ".itemDetailPage:not(.hide) .additionalMaterialContents"
    button = page.locator(MAIN)
    try:
        button.wait_for(timeout=20000)
        check("button appears on a course with material", True)
    except Exception as e:  # noqa: BLE001
        check("button appears on a course with material", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-course.png")
    if button.count():
        check("button is enabled for a user with download permission", button.is_enabled())
        check("course button names its own archive", "ZIP" in (button.get_attribute("title") or ""), button.get_attribute("title"))
        with page.expect_download(timeout=30000) as d:
            button.click()
        check("course button downloads the course archive directly", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected)
        cb = page.locator(CONTENTS)
        check("course has a Contents button counting all archives", cb.count() == 1 and "3 archives" in (cb.get_attribute("title") or ""),
              cb.get_attribute("title") if cb.count() else "missing")
        cb.click()
        dialog = page.locator(".am-dialog")
        try:
            dialog.wait_for(timeout=10000)
            check("Contents button opens the listing", True)
        except Exception as e:  # noqa: BLE001
            check("Contents button opens the listing", False, str(e)[:120])
        check("listing has course, section and lesson rows", dialog.locator(".am-row").count() == 3, str(dialog.locator(".am-row").count()))
        with page.expect_download(timeout=30000) as d:
            dialog.locator(".am-row").nth(0).locator(".am-download").click()
        check("listing downloads the course archive", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected)
        lesson_block = dialog.locator(".am-archive").nth(2)   # names may come from online metadata; the lesson is last
        lesson_block.locator(".am-contents-toggle").click()
        try:
            lesson_block.locator(".am-file").first.wait_for(timeout=10000)
            names = lesson_block.locator(".am-file .am-name").all_inner_texts()
            check("expanding a lesson shows its files", any("notes.txt" in n for n in names) and any("intro.txt" in n for n in names), str(names))
        except Exception as e:  # noqa: BLE001
            check("expanding a lesson shows its files", False, str(e)[:120])
        page.screenshot(path="/t/screenshot-contents.png")
        check("folders are shown as folders", lesson_block.locator(".am-folder .am-name", has_text="slides/").count() == 1)
        removed = lesson_block.locator(".am-removed")
        check("a removed file shows its name and that it was replaced", removed.count() == 1 and "tool.exe" in removed.inner_text(), removed.inner_text() if removed.count() else "")
        with page.expect_download(timeout=30000) as d:
            lesson_block.locator(".am-file", has_text="notes.txt").locator(".am-file-download").click()
        check("one file downloads on its own", open(d.value.path()).read().strip() == "material for S01E01" and d.value.suggested_filename == "notes.txt",
              d.value.suggested_filename)
        lab = lesson_block.locator(".am-file", has_text="labs.zip")
        lab.locator(".am-caret").click()
        inner = lesson_block.locator(".am-file", has_text="lab1.txt")
        try:
            inner.wait_for(timeout=5000)
            with page.expect_download(timeout=30000) as d:
                inner.locator(".am-file-download").click()
            check("a file inside a nested zip downloads", open(d.value.path()).read().strip() == "lab one")
        except Exception as e:  # noqa: BLE001
            check("a file inside a nested zip downloads", False, str(e)[:120])
        with page.expect_download(timeout=30000) as d:
            dialog.locator(".am-row").nth(2).locator(".am-download").click()
        check("listing downloads a lesson archive", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected_lesson)
        dialog.locator(".am-row").nth(2).locator(".am-goto").click()
        page.wait_for_timeout(1500)
        check("Go to lesson opens the lesson page", lesson1 in page.url.replace("-", ""), page.url)
        check("listing closes on Go to lesson", page.locator(".am-dialog").count() == 0)
        page.wait_for_selector(MAIN, timeout=20000)
        lb = page.locator(MAIN)
        check("lesson button names format and size", "ZIP" in (lb.get_attribute("title") or ""), lb.get_attribute("title"))
        check("lesson has no separate Contents button", page.locator(CONTENTS).count() == 0)
        lb.click()
        try:
            page.locator(".am-dialog .am-single .am-file").first.wait_for(timeout=10000)
            check("lesson button opens its contents", True)
        except Exception as e:  # noqa: BLE001
            check("lesson button opens its contents", False, str(e)[:120])
        page.screenshot(path="/t/screenshot-lesson-contents.png")
        with page.expect_download(timeout=30000) as d:
            page.locator(".am-download-all").click()
        check("Download all downloads the lesson archive", hashlib.sha256(open(d.value.path(), "rb").read()).hexdigest() == expected_lesson)
        page.keyboard.press("Escape")
        open_details(page, series)
        page.wait_for_selector(MAIN, timeout=20000)
    if button.count():
        color = page.evaluate("() => getComputedStyle(document.querySelector('.additionalMaterialIcon')).getPropertyValue('--am-accent').trim()")
        check("two-color style puts the accent on the badge", color.lower() == "#db781b", color)
        check("button shows the A6 icon (inline SVG)", page.locator(".additionalMaterialButton svg").count() == 1)
    open_details(page, plain)
    page.wait_for_timeout(3000)
    check("no button on an item without material", page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton").count() == 0)
    open_details(page, series)
    page.wait_for_timeout(3000)
    check("one download and one Contents button after returning to the course",
          page.locator(".additionalMaterialButton:not(.additionalMaterialContents)").count() == 1 and page.locator(".additionalMaterialContents").count() == 1,
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

    # Jellyfin (React) re-builds a card's indicator row, or a row's buttons, when its data refreshes.
    # The icon must come back (1.2.2 had marked the element done and never re-added it).
    def rebuilt(view, url, items, icon, wipe):
        pg = ctx.new_page()
        pg.goto(url)
        name = f"{view}: icon comes back after Jellyfin re-builds the element"
        try:
            pg.wait_for_selector(f"{items} {icon}", timeout=30000)
            pg.evaluate(wipe)
            pg.wait_for_timeout(100)
            gone = pg.locator(f"{items} {icon}").count() == 0
            pg.wait_for_selector(f"{items} {icon}", timeout=10000)
            check(name, gone, "the wipe did not remove the icon")
        except Exception as e:  # noqa: BLE001
            check(name, False, str(e)[:120])
        pg.close()

    rebuilt("grid", f"{BASE}/web/#/tv?topParentId={training}&serverId={server['Id']}", ".card[data-id]", ".additionalMaterialIndicator",
            "() => document.querySelectorAll('.card[data-id] .cardIndicators').forEach(c => { c.innerHTML = ''; })")
    rebuilt("list", f"{BASE}/web/#/details?id={season1}&serverId={server['Id']}", ".listItem[data-id]", ".additionalMaterialListButton",
            "() => document.querySelectorAll('.additionalMaterialListButton').forEach(b => b.remove())")

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
        page.locator("#amReread").click()
        page.wait_for_function("() => /Indexed \\d+ folders, \\d+ zips/.test(document.querySelector('#amIndexStatus').textContent)", timeout=30000)
        check("Re-read folders runs and reports the result", True)
        check("settings page has the nested-zip setting, on", page.locator("#amNested").is_checked())
    except Exception as e:  # noqa: BLE001
        check("settings page shows the icon preview", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-settings.png", full_page=True)
    ctx.close()

    ctx, page = session(browser, reader_token, reader_id)
    open_details(page, series)
    try:
        page.locator(MAIN).wait_for(timeout=20000)
        check("no download permission: course download button is disabled", not page.locator(MAIN).is_enabled())
        page.locator(CONTENTS).click()
        page.locator(".am-dialog").wait_for(timeout=10000)
        disabled = page.evaluate("() => [...document.querySelectorAll('.am-download')].every(b => b.disabled)")
        check("no download permission: listing's Download buttons are disabled", disabled)
        check("no download permission: listing says so", page.locator(".am-note").count() >= 1)
    except Exception as e:  # noqa: BLE001
        check("no download permission: listing's Download buttons are disabled", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-no-permission.png")
    open_details(page, lesson1)
    check("leaving the page closes the dialog", page.locator(".am-dialog").count() == 0)
    try:
        lb = page.locator(MAIN)
        lb.wait_for(timeout=20000)
        lb.click()
        page.locator(".am-dialog .am-single .am-file").first.wait_for(timeout=10000)
        check("no download permission: contents still shown", True)
        disabled = page.evaluate("() => [...document.querySelectorAll('.am-file-download, .am-download-all')].every(b => b.disabled)")
        check("no download permission: file and Download all buttons are disabled", disabled)
    except Exception as e:  # noqa: BLE001
        check("no download permission: contents still shown", False, str(e)[:120])
    ctx.close()
    browser.close()

print(f"\npassed {sum(results)}, failed {len(results) - sum(results)}")
sys.exit(0 if all(results) else 1)
