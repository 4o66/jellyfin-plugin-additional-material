/*
 * Additional Material for Jellyfin: shows a download icon for course material.
 *   - item pages: a button next to Play; on courses and sections it lists the material below
 *   - grid views: an indicator on cards (top right, after the unwatched count)
 *   - list views: a button beside the favorite heart
 * Plain script, no build step. It only adds elements; it never patches the web client's bundles.
 */
(function () {
    'use strict';

    if (window.__additionalMaterialLoaded) {
        return;
    }
    window.__additionalMaterialLoaded = true;

    var BUTTON_CLASS = 'additionalMaterialButton';
    var ICON = '<mask id="__ID__" maskUnits="userSpaceOnUse" x="-2" y="-2" width="28" height="28"><rect x="-2" y="-2" width="28" height="28" fill="#fff"/><circle cx="17.6" cy="17.4" r="6.6" fill="#000"/></mask><g mask="url(#__ID__)"><path d="M9.17 6l2 2H20v10H4V6h5.17M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z" fill="currentColor" fill-rule="evenodd"/><polyline points="8.65,11.60 10.35,12.90 8.65,14.20 10.35,15.50 8.65,16.80" fill="none" stroke="currentColor" stroke-width="1.35" stroke-linejoin="miter"/><rect x="8.0" y="8.0" width="3" height="3.1" rx="1.1" fill="currentColor"/></g><circle cx="17.6" cy="17.4" r="4.9" fill="none" stroke="var(--am-accent, currentColor)" stroke-width="1.7"/><path d="M15.3 17.4H19.900000000000002M17.6 15.099999999999998V19.7" stroke="var(--am-accent, currentColor)" stroke-width="1.7" fill="none"/>';
    var strings = {
        'button.label': 'Additional material ({format}, {size})',
        'button.noPermission': '{label}: downloads are not enabled for your account',
        'button.failed': 'The download could not be started. Try again in a moment.',
        'button.count': 'Additional material: {count} archives',
        'dialog.title': 'Additional material',
        'dialog.close': 'Close',
        'dialog.this.Series': 'This course',
        'dialog.this.Season': 'This section',
        'dialog.this.Episode': 'This lesson',
        'dialog.this.Movie': 'This video',
        'dialog.this.other': 'This item',
        'dialog.section': 'Section material',
        'dialog.goto': 'Go to lesson',
        'dialog.gotoSection': 'Go to section',
        'dialog.download': 'Download',
        'dialog.noPermission': 'Downloads are not enabled for your account.',
        'dialog.empty': 'No additional material here.'
    };
    var settings = { ButtonStyle: 'color', AccentColor: '#00A4DC', ShowOnParents: 'all', ShowOnCards: true, ShowInLists: true };
    var ready = null;
    var treeCache = {};
    // Settled answers only: an item's status, or false for "no material". Requests still on the
    // way live in statusInFlight; mixing the two once let cards that Jellyfin re-drew mid-request
    // read "still asking" as "nothing here" and never get their icon.
    var statusCache = {};
    var statusInFlight = {};
    var statusRetry = 0;
    var FAILED = {};
    var iconCount = 0;
    var scheduled = false;

    // ---- helpers -------------------------------------------------------------------------------
    function apiClient() {
        return window.ApiClient && typeof window.ApiClient.getUrl === 'function' ? window.ApiClient : null;
    }

    function authHeader(client) {
        var token = typeof client.accessToken === 'function' ? client.accessToken() : null;
        return token ? { Authorization: 'MediaBrowser Token="' + token + '"' } : {};
    }

    function t(key, values) {
        var text = strings[key] || key;
        Object.keys(values || {}).forEach(function (name) {
            text = text.split('{' + name + '}').join(values[name]);
        });
        return text;
    }

    function getJson(client, path, init) {
        var opts = init || {};
        opts.headers = Object.assign({}, authHeader(client), opts.headers || {});
        return fetch(client.getUrl(path), opts).then(function (r) {
            if (!r.ok) {
                throw new Error('HTTP ' + r.status);
            }
            return r.json();
        });
    }

    function load(client) {
        if (!ready) {
            var lang = document.documentElement.getAttribute('lang') || navigator.language || 'en';
            // Settings decide where icons go, so wait for them (one small request). Translations
            // only change wording: give them a moment, then draw in English rather than wait.
            var translations = fetch(client.getUrl('AdditionalMaterial/web/strings', { lang: lang })).then(function (r) { return r.ok ? r.json() : {}; })
                .then(function (loaded) { Object.assign(strings, loaded); }).catch(function () { /* built-in English */ });
            ready = Promise.all([
                Promise.race([translations, new Promise(function (resolve) { window.setTimeout(resolve, 300); })]),
                getJson(client, 'AdditionalMaterial/web/settings').then(function (s) { Object.assign(settings, s); }).catch(function () { /* defaults */ })
            ]);
        }
        return ready;
    }

    function formatSize(bytes) {
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.length - 1) {
            size /= 1024;
            unit++;
        }
        return (unit === 0 ? size : size.toFixed(1)) + ' ' + units[unit];
    }

    function el(tag, className, text) {
        var e = document.createElement(tag);
        if (className) {
            e.className = className;
        }
        if (text !== undefined) {
            e.textContent = text;
        }
        return e;
    }

    // Design A6 (assets/make_icons.py): folder and zipper in the text color, plus badge in
    // var(--am-accent), which is the text color too in the one-color style.
    function iconElement(className) {
        iconCount++;
        var span = el('span', className);
        span.style.display = 'inline-flex';
        if (settings.ButtonStyle === 'color' && /^#[0-9A-Fa-f]{6}$/.test(settings.AccentColor || '')) {
            span.style.setProperty('--am-accent', settings.AccentColor);
        }
        span.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="1em" height="1em" aria-hidden="true" focusable="false">' +
            ICON.split('__ID__').join('am-cut-' + iconCount) + '</svg>';
        return span;
    }

    function currentItemId() {
        var text = window.location.hash + '&' + window.location.search;
        var match = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(text);
        return match && /details/i.test(window.location.hash + window.location.pathname) ? match[1].replace(/-/g, '').toLowerCase() : null;
    }

    function tree(client, itemId) {
        if (!treeCache[itemId]) {
            treeCache[itemId] = getJson(client, 'AdditionalMaterial/Items/' + itemId + '/Tree')
                .catch(function () { delete treeCache[itemId]; return null; });
        }
        return treeCache[itemId];
    }

    function countOf(tr) {
        if (!tr) {
            return 0;
        }
        return (tr.Self ? 1 : 0) + tr.Groups.reduce(function (n, g) { return n + g.Items.length; }, 0);
    }

    function download(client, itemId, button) {
        if (button) {
            button.disabled = true;
        }
        return getJson(client, 'AdditionalMaterial/Items/' + itemId + '/Link', { method: 'POST' })
            .then(function (link) {
                var a = document.createElement('a');
                a.href = client.getUrl('AdditionalMaterial/Download/' + link.Token);
                a.rel = 'noopener noreferrer';
                a.setAttribute('download', '');
                document.body.appendChild(a);
                a.click();
                a.remove();
            })
            .catch(function (err) {
                window.console && console.warn('Additional Material: download failed', err);
                if (button) {
                    button.title = t('button.failed');
                }
            })
            .finally(function () {
                if (button) {
                    button.disabled = false;
                }
            });
    }

    // Open the listing for an item, or download right away when it has exactly its own archive.
    function activate(client, itemId, button) {
        return tree(client, itemId).then(function (tr) {
            if (!tr || countOf(tr) === 0) {
                return;
            }
            if (tr.Self && tr.Groups.length === 0) {
                if (tr.CanDownload) {
                    download(client, itemId, button);
                }
                return;
            }
            openDialog(client, tr);
        });
    }

    // ---- the listing dialog ----------------------------------------------------------------------
    function injectStyles() {
        if (document.getElementById('additionalMaterialStyles')) {
            return;
        }
        var style = el('style');
        style.id = 'additionalMaterialStyles';
        style.textContent = [
            '.am-backdrop{position:fixed;inset:0;z-index:2000;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:1em;}',
            '.am-dialog{background:#202020;color:#e6e6e6;border-radius:.5em;width:min(46em,100%);max-height:85vh;display:flex;flex-direction:column;box-shadow:0 1em 3em rgba(0,0,0,.5);}',
            '.am-head{display:flex;align-items:center;gap:.6em;padding:1em 1.2em .6em;font-size:1.1em;}',
            '.am-head .am-title{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
            '.am-head .am-title small{display:block;opacity:.6;font-size:.8em;}',
            '.am-body{overflow:auto;padding:0 1.2em 1.2em;}',
            '.am-group{margin-top:.9em;}',
            '.am-group h3{font-size:.85em;font-weight:500;letter-spacing:.04em;text-transform:uppercase;opacity:.65;margin:0 0 .3em;}',
            '.am-row{display:flex;align-items:center;gap:.6em;padding:.45em 0;border-top:1px solid rgba(255,255,255,.08);}',
            '.am-row .am-n{width:2.2em;opacity:.6;font-variant-numeric:tabular-nums;}',
            '.am-row .am-name{flex:1;min-width:0;}',
            '.am-row .am-size{opacity:.6;font-size:.9em;white-space:nowrap;font-variant-numeric:tabular-nums;}',
            '.am-row button{background:none;border:1px solid rgba(255,255,255,.25);color:inherit;border-radius:.3em;padding:.3em .7em;font:inherit;font-size:.85em;cursor:pointer;white-space:nowrap;}',
            '.am-row button:hover,.am-row button:focus-visible{border-color:#00a4dc;outline:none;}',
            '.am-row button:disabled{opacity:.4;cursor:default;}',
            '.am-close{background:none;border:none;color:inherit;font-size:1.4em;cursor:pointer;line-height:1;padding:.2em;}',
            '.am-note{opacity:.7;font-size:.9em;margin:.4em 0 0;}',
            '.additionalMaterialIndicator{background:rgba(0,0,0,.7);color:#fff;cursor:pointer;font-size:1.25em;width:1.6em;height:1.6em;}',
            '.additionalMaterialIndicator svg{width:1em;height:1em;}',
            '.additionalMaterialListButton .additionalMaterialIcon{font-size:1.67em;}',
            '@media (max-width:30em){.am-row{flex-wrap:wrap}.am-row .am-name{flex-basis:calc(100% - 3em)}}'
        ].join('');
        document.head.appendChild(style);
    }

    function closeDialog() {
        var d = document.querySelector('.am-backdrop');
        if (d) {
            d.remove();
        }
        document.removeEventListener('keydown', onKey, true);
    }

    function onKey(e) {
        if (e.key === 'Escape') {
            e.stopPropagation();
            closeDialog();
        }
    }

    function goTo(client, itemId) {
        closeDialog();
        var serverId = typeof client.serverId === 'function' ? client.serverId() : '';
        window.location.hash = '#/details?id=' + itemId + (serverId ? '&serverId=' + serverId : '');
    }

    function row(client, tr, item, label, gotoLabel) {
        var r = el('div', 'am-row');
        r.appendChild(el('span', 'am-n', item.IndexNumber !== null && item.IndexNumber !== undefined && item.Level === 'lesson' ? String(item.IndexNumber) : ''));
        r.appendChild(el('span', 'am-name', label));
        r.appendChild(el('span', 'am-size', 'ZIP ' + formatSize(item.Size)));
        if (gotoLabel) {
            var go = el('button', 'am-goto', gotoLabel);
            go.type = 'button';
            go.addEventListener('click', function () { goTo(client, item.ItemId); });
            r.appendChild(go);
        }
        var dl = el('button', 'am-download', t('dialog.download'));
        dl.type = 'button';
        dl.disabled = !tr.CanDownload;
        dl.addEventListener('click', function () { download(client, item.ItemId, dl); });
        r.appendChild(dl);
        return r;
    }

    function openDialog(client, tr) {
        closeDialog();
        injectStyles();
        var backdrop = el('div', 'am-backdrop');
        var dialog = el('div', 'am-dialog');
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        var head = el('div', 'am-head');
        head.appendChild(iconElement('am-icon'));
        var title = el('div', 'am-title', t('dialog.title'));
        title.appendChild(el('small', '', tr.Name));
        head.appendChild(title);
        var close = el('button', 'am-close', '×');
        close.type = 'button';
        close.title = t('dialog.close');
        close.setAttribute('aria-label', t('dialog.close'));
        close.addEventListener('click', closeDialog);
        head.appendChild(close);
        dialog.appendChild(head);
        var body = el('div', 'am-body');
        if (!tr.CanDownload) {
            body.appendChild(el('p', 'am-note', t('dialog.noPermission')));
        }
        if (tr.Self) {
            var own = el('div', 'am-group');
            own.appendChild(el('h3', '', t('dialog.this.' + tr.Type) !== 'dialog.this.' + tr.Type ? t('dialog.this.' + tr.Type) : t('dialog.this.other')));
            own.appendChild(row(client, tr, tr.Self, tr.Self.FileName, null));
            body.appendChild(own);
        }
        tr.Groups.forEach(function (g) {
            var group = el('div', 'am-group');
            group.appendChild(el('h3', '', (g.IndexNumber !== null && g.IndexNumber !== undefined ? g.IndexNumber + ' · ' : '') + (g.Name || '')));
            g.Items.forEach(function (item) {
                if (item.Level === 'section') {
                    group.appendChild(row(client, tr, item, t('dialog.section'), t('dialog.gotoSection')));
                } else {
                    group.appendChild(row(client, tr, item, item.Name, t('dialog.goto')));
                }
            });
            body.appendChild(group);
        });
        if (countOf(tr) === 0) {
            body.appendChild(el('p', 'am-note', t('dialog.empty')));
        }
        dialog.appendChild(body);
        backdrop.appendChild(dialog);
        backdrop.addEventListener('click', function (e) {
            if (e.target === backdrop) {
                closeDialog();
            }
        });
        document.body.appendChild(backdrop);
        document.addEventListener('keydown', onKey, true);
        close.focus();
    }

    // ---- item page button ------------------------------------------------------------------------
    function makeButton(client, itemId, tr) {
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'emby-button button-flat detailButton ' + BUTTON_CLASS;
        button.dataset.itemId = itemId;
        var count = countOf(tr);
        var label;
        if (tr.Self && tr.Groups.length === 0) {
            label = t('button.label', { format: 'ZIP', size: formatSize(tr.Self.Size) });
            if (!tr.CanDownload) {
                label = t('button.noPermission', { label: label });
                button.disabled = true;
            }
        } else {
            label = t('button.count', { count: count });
        }
        button.title = label;
        button.setAttribute('aria-label', label);
        var content = el('div', 'detailButton-content');
        content.appendChild(iconElement('detailButton-icon additionalMaterialIcon'));
        button.appendChild(content);
        button.addEventListener('click', function () { activate(client, itemId, button); });
        return button;
    }

    function updateDetailPage(client) {
        var itemId = currentItemId();
        var page = document.querySelector('.itemDetailPage:not(.hide)');
        document.querySelectorAll('.' + BUTTON_CLASS).forEach(function (b) {
            if (!itemId || b.dataset.itemId !== itemId || !page || !page.contains(b)) {
                b.remove();
            }
        });
        if (!itemId || !page) {
            return;
        }
        var bar = page.querySelector('.mainDetailButtons');
        if (!bar || bar.querySelector('.' + BUTTON_CLASS + '[data-item-id="' + itemId + '"]')) {
            return;
        }
        tree(client, itemId).then(function (tr) {
            if (!tr || countOf(tr) === 0 || currentItemId() !== itemId) {
                return;
            }
            if (bar.isConnected && !bar.querySelector('.' + BUTTON_CLASS + '[data-item-id="' + itemId + '"]')) {
                var anchor = bar.querySelector('.btnDownload');
                bar.insertBefore(makeButton(client, itemId, tr), anchor ? anchor.nextSibling : null);
            }
        }).catch(function (err) {
            window.console && console.warn('Additional Material: could not add the button', err);
        });
    }

    // ---- grid cards and list rows ----------------------------------------------------------------
    function normId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function decorate(client, elem, kind, status) {
        var id = normId(elem.getAttribute('data-id'));
        if (elem.dataset.amDone === id) {
            return;
        }
        elem.dataset.amDone = id;
        if (!status) {  // false: settled, no material
            return;
        }
        var label = status.Own && status.Below === 0 ? t('dialog.title') : t('button.count', { count: (status.Own ? 1 : 0) + status.Below });
        var handler = function (e) {
            e.preventDefault();
            e.stopPropagation();
            activate(client, id, null);
        };
        if (kind === 'card') {
            var container = elem.querySelector('.cardImageContainer') || elem.querySelector('.cardScalable');
            if (!container) {
                return;
            }
            var indicators = container.querySelector('.cardIndicators');
            if (!indicators) {
                indicators = el('div', 'cardIndicators');
                container.appendChild(indicators);
            }
            var ind = iconElement('indicator additionalMaterialIndicator');
            ind.title = label;
            ind.setAttribute('role', 'button');
            ind.setAttribute('aria-label', label);
            ind.tabIndex = 0;
            // Clicks normally land on the card's hover overlay, which sits above the indicators;
            // the page-level listener below finds the indicator under the pointer instead.
            ind.dataset.amItem = id;
            ind.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { handler(e); } });
            indicators.appendChild(ind);   // last in the row: the corner, pushing the count left
        } else {
            var buttons = elem.querySelector('.listViewUserDataButtons');
            if (!buttons) {
                return;
            }
            var b = el('button', 'listItemButton paper-icon-button-light additionalMaterialListButton');
            b.type = 'button';
            b.title = label;
            b.setAttribute('aria-label', label);
            b.appendChild(iconElement('additionalMaterialIcon'));
            b.addEventListener('click', handler, true);
            var heart = buttons.querySelector('[is="emby-ratingbutton"]');
            buttons.insertBefore(b, heart || null);
        }
    }

    function updateLists(client) {
        var targets = [];
        if (settings.ShowOnCards) {
            document.querySelectorAll('.card[data-id]').forEach(function (c) { targets.push([c, 'card']); });
        }
        if (settings.ShowInLists) {
            document.querySelectorAll('.listItem[data-id]').forEach(function (c) { targets.push([c, 'list']); });
        }
        var pending = targets.filter(function (x) { return x[0].dataset.amDone !== normId(x[0].getAttribute('data-id')); });
        if (!pending.length) {
            return;
        }
        var ask = [];
        var waits = [];
        pending.forEach(function (x) {
            var id = normId(x[0].getAttribute('data-id'));
            if (id in statusCache) {
                decorate(client, x[0], x[1], statusCache[id]);
            } else if (id in statusInFlight) {
                if (waits.indexOf(statusInFlight[id]) < 0) {
                    waits.push(statusInFlight[id]);
                }
            } else if (ask.indexOf(id) < 0) {
                ask.push(id);
            }
        });
        for (var i = 0; i < ask.length; i += 200) {
            (function (chunk) {
                var request = getJson(client, 'AdditionalMaterial/Items/Status', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ Ids: chunk })
                }).then(function (res) {
                    var found = {};
                    Object.keys(res || {}).forEach(function (k) { found[normId(k)] = res[k]; });
                    chunk.forEach(function (id) { statusCache[id] = found[id] || false; });
                    statusRetry = 0;
                }, function () {
                    // Try again shortly (backing off), rather than waiting for the page to change.
                    statusRetry = Math.min(statusRetry ? statusRetry * 2 : 2000, 30000);
                    window.setTimeout(schedule, statusRetry);
                    return FAILED;
                }).then(function (r) {
                    chunk.forEach(function (id) { delete statusInFlight[id]; });
                    if (r === FAILED) {
                        throw r;
                    }
                });
                chunk.forEach(function (id) { statusInFlight[id] = request; });
                waits.push(request);
            })(ask.slice(i, i + 200));
        }
        if (waits.length) {
            // Decorate whatever cards are on the page once the answers are in: Jellyfin may have
            // replaced the ones that were there when the request went out.
            Promise.all(waits).then(schedule, function () { /* the retry timer handles it */ });
        }
    }

    // ---- wiring ----------------------------------------------------------------------------------
    function update() {
        scheduled = false;
        var client = apiClient();
        if (!client || (typeof client.accessToken === 'function' && !client.accessToken())) {
            return;
        }
        load(client).then(function () {
            injectStyles();
            updateDetailPage(client);
            updateLists(client);
        });
    }

    function schedule() {
        if (!scheduled) {
            scheduled = true;
            window.setTimeout(update, 200);
        }
    }

    function indicatorAt(e) {
        if (typeof document.elementsFromPoint !== 'function' || e.clientX === undefined) {
            return null;
        }
        var hits = document.elementsFromPoint(e.clientX, e.clientY);
        for (var i = 0; i < hits.length; i++) {
            var ind = hits[i].closest && hits[i].closest('.additionalMaterialIndicator');
            if (ind) {
                return ind;
            }
        }
        return null;
    }

    function onPointer(e) {
        var ind = indicatorAt(e);
        if (!ind) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        if (e.type === 'click') {
            var client = apiClient();
            if (client) {
                activate(client, ind.dataset.amItem, null);
            }
        }
    }

    // Capture phase, before the card's own click handling navigates to the item.
    ['click', 'mousedown', 'pointerdown', 'mouseup', 'pointerup'].forEach(function (name) {
        document.addEventListener(name, onPointer, true);
    });

    ['hashchange', 'popstate', 'viewshow', 'pageshow'].forEach(function (name) {
        window.addEventListener(name, schedule);
        document.addEventListener(name, schedule, true);
    });
    // Material can change after the plugin's server-side cache expires; refresh lookups now and then.
    window.setInterval(function () { treeCache = {}; statusCache = {}; }, 120000);
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    schedule();
})();
