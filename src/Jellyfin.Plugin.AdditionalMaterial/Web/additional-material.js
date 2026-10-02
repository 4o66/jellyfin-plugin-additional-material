/*
 * Additional Material for Jellyfin: adds a download button to item detail pages when the item has
 * an additional-material archive. Plain script, no build step. It only adds to the page: it does not
 * patch the web client's bundles, so web-client updates can at worst hide the button.
 */
(function () {
    'use strict';

    if (window.__additionalMaterialLoaded) {
        return;
    }
    window.__additionalMaterialLoaded = true;

    var BUTTON_CLASS = 'additionalMaterialButton';
    // Text comes from the server in the web client's display language (Web/i18n/<lang>.json).
    var strings = {
        'button.label': 'Additional material ({format}, {size})',
        'button.noPermission': '{label}: downloads are not enabled for your account',
        'button.failed': 'The download could not be started. Try again in a moment.'
    };
    var stringsLoaded = null;

    function t(key, values) {
        var text = strings[key] || key;
        Object.keys(values || {}).forEach(function (name) {
            text = text.split('{' + name + '}').join(values[name]);
        });
        return text;
    }

    function loadStrings(client) {
        if (!stringsLoaded) {
            var lang = document.documentElement.getAttribute('lang') || navigator.language || 'en';
            stringsLoaded = fetch(client.getUrl('AdditionalMaterial/web/strings', { lang: lang }))
                .then(function (r) { return r.ok ? r.json() : {}; })
                .then(function (loaded) { Object.keys(loaded).forEach(function (k) { strings[k] = loaded[k]; }); })
                .catch(function () { /* keep the built-in English */ });
        }
        return stringsLoaded;
    }
    var cache = {};
    var scheduled = false;

    function apiClient() {
        return window.ApiClient && typeof window.ApiClient.getUrl === 'function' ? window.ApiClient : null;
    }

    function authHeader(client) {
        var token = typeof client.accessToken === 'function' ? client.accessToken() : null;
        return token ? { Authorization: 'MediaBrowser Token="' + token + '"' } : {};
    }

    function currentItemId() {
        var text = window.location.hash + '&' + window.location.search;
        var match = /[?&]id=([0-9a-fA-F-]{32,36})/.exec(text);
        return match && /details/i.test(window.location.hash + window.location.pathname) ? match[1].replace(/-/g, '').toLowerCase() : null;
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

    function fetchInfo(client, itemId) {
        if (!cache[itemId]) {
            cache[itemId] = fetch(client.getUrl('AdditionalMaterial/Items/' + itemId), { headers: authHeader(client) })
                .then(function (r) { return r.ok ? r.json() : { Available: false }; })
                .catch(function () { delete cache[itemId]; return { Available: false }; });
        }
        return cache[itemId];
    }

    function download(client, itemId, button) {
        button.disabled = true;
        fetch(client.getUrl('AdditionalMaterial/Items/' + itemId + '/Link'), { method: 'POST', headers: authHeader(client) })
            .then(function (r) {
                if (!r.ok) {
                    throw new Error('HTTP ' + r.status);
                }
                return r.json();
            })
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
                button.title = t('button.failed');
            })
            .finally(function () { button.disabled = false; });
    }

    function makeButton(client, itemId, info) {
        // A plain button with the web client's classes. Creating it as jellyfin-web's own
        // "emby-button" element runs internal code that throws on elements it did not render.
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'emby-button button-flat detailButton ' + BUTTON_CLASS;
        button.dataset.itemId = itemId;
        var label = t('button.label', { format: String(info.Format || '').toUpperCase(), size: formatSize(info.Size) });
        if (!info.CanDownload) {
            label = t('button.noPermission', { label: label });
            button.disabled = true;
        }
        button.title = label;
        button.setAttribute('aria-label', label);
        var content = document.createElement('div');
        content.className = 'detailButton-content';
        var icon = document.createElement('span');
        icon.className = 'material-icons detailButton-icon attach_file';
        icon.setAttribute('aria-hidden', 'true');
        content.appendChild(icon);
        button.appendChild(content);
        if (info.CanDownload) {
            button.addEventListener('click', function () { download(client, itemId, button); });
        }
        return button;
    }

    function update() {
        scheduled = false;
        var client = apiClient();
        var itemId = currentItemId();
        var page = document.querySelector('.itemDetailPage:not(.hide)');
        document.querySelectorAll('.' + BUTTON_CLASS).forEach(function (b) {
            if (!itemId || b.dataset.itemId !== itemId || !page || !page.contains(b)) {
                b.remove();
            }
        });
        if (!client || !itemId || !page) {
            return;
        }
        var bar = page.querySelector('.mainDetailButtons');
        if (!bar || bar.querySelector('.' + BUTTON_CLASS + '[data-item-id="' + itemId + '"]')) {
            return;
        }
        Promise.all([fetchInfo(client, itemId), loadStrings(client)]).then(function (results) {
            var info = results[0];
            if (!info || !info.Available || currentItemId() !== itemId) {
                return;
            }
            if (bar.isConnected && !bar.querySelector('.' + BUTTON_CLASS + '[data-item-id="' + itemId + '"]')) {
                var anchor = bar.querySelector('.btnDownload');
                bar.insertBefore(makeButton(client, itemId, info), anchor ? anchor.nextSibling : null);
            }
        }).catch(function (err) {
            window.console && console.warn('Additional Material: could not add the button', err);
        });
    }

    function schedule() {
        if (!scheduled) {
            scheduled = true;
            window.setTimeout(update, 150);
        }
    }

    ['hashchange', 'popstate', 'viewshow', 'pageshow'].forEach(function (name) {
        window.addEventListener(name, schedule);
        document.addEventListener(name, schedule, true);
    });
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    schedule();
})();
