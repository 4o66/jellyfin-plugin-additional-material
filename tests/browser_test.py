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


def api(path, token=None, data=None):
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
        check("tooltip names the format and size", "ZIP" in (button.get_attribute("title") or ""), button.get_attribute("title"))
        with page.expect_download(timeout=30000) as d:
            button.click()
        path = d.value.path()
        check("click downloads the archive", d.value.suggested_filename == "additional-material.zip", d.value.suggested_filename)
        check("downloaded bytes match", hashlib.sha256(open(path, "rb").read()).hexdigest() == expected)
    open_details(page, plain)
    page.wait_for_timeout(3000)
    check("no button on an item without material", page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton").count() == 0)
    open_details(page, series)
    page.wait_for_timeout(3000)
    check("exactly one button after returning to the course", page.locator(".additionalMaterialButton").count() == 1,
          str(page.locator(".additionalMaterialButton").count()))
    ctx.close()

    ctx, page = session(browser, reader_token, reader_id)
    open_details(page, series)
    button = page.locator(".itemDetailPage:not(.hide) .additionalMaterialButton")
    try:
        button.wait_for(timeout=20000)
        check("user without download permission sees the button disabled", not button.is_enabled())
    except Exception as e:  # noqa: BLE001
        check("user without download permission sees the button disabled", False, str(e)[:120])
    page.screenshot(path="/t/screenshot-no-permission.png")
    ctx.close()
    browser.close()

print(f"\npassed {sum(results)}, failed {len(results) - sum(results)}")
sys.exit(0 if all(results) else 1)
