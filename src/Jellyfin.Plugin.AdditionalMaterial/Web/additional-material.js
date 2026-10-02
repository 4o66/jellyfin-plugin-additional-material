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
        'dialog.empty': 'No additional material here.',
        'button.contentsOf': 'Additional material ({format}, {size}): show contents',
        'button.countOne': 'Additional material: 1 archive',
        'dialog.downloadLevel.Series': 'Download the course archive ({format}, {size})',
        'dialog.downloadLevel.Season': 'Download the section archive ({format}, {size})',
        'dialog.downloadLevel.other': 'Download this archive ({format}, {size})',
        'dialog.contents': 'Contents',
        'dialog.downloadAll': 'Download all (.zip, {size})',
        'dialog.downloadFile': 'Download {name}',
        'dialog.file': '1 file',
        'dialog.files': '{count} files',
        'dialog.removed': 'removed, replaced by a note',
        'dialog.nestedTooLarge': 'too large to list',
        'dialog.truncated': 'Showing the first {count} files.',
        'dialog.loading': 'Loading…',
        'dialog.loadFailed': 'The contents could not be read. Try again in a moment.',
        'dialog.emptyArchive': 'This archive is empty.',
        'dialog.leftOut': 'left out: {reason}',
        'dialog.downloadOne': 'Download {name} ({size})',
        'dialog.openLink': 'Open link \u2197',
        'dialog.opensSite': 'a link to {host}'
    };
    var settings = { ButtonStyle: 'color', AccentColor: '#00A4DC', ShowOnParents: 'all', ShowOnCards: true, ShowInLists: true };
    var ready = null;
    var treeCache = {};
    var contentsCache = {};
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

    // "ZIP" for an archive, else the file's own type: one-file material is handed out as is.
    function formatOf(name) {
        var dot = (name || '').lastIndexOf('.');
        return dot > 0 ? name.slice(dot + 1).toUpperCase() : 'ZIP';
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

    // Downloads an item's archive, or with entry, one file inside it.
    function download(client, itemId, button, entry) {
        if (button) {
            button.disabled = true;
        }
        return getJson(client, 'AdditionalMaterial/Items/' + itemId + '/Link', { method: 'POST' })
            .then(function (link) {
                var a = document.createElement('a');
                a.href = client.getUrl('AdditionalMaterial/Download/' + link.Token) + (entry ? '?entry=' + encodeURIComponent(entry) : '');
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

    function contents(client, itemId) {
        if (!contentsCache[itemId]) {
            contentsCache[itemId] = getJson(client, 'AdditionalMaterial/Items/' + itemId + '/Contents')
                .catch(function (err) { delete contentsCache[itemId]; throw err; });
        }
        return contentsCache[itemId];
    }

    // Open the contents view for an item.
    function activate(client, itemId) {
        return tree(client, itemId).then(function (tr) {
            if (tr && countOf(tr) > 0) {
                openDialog(client, tr);
            }
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
            '.am-tree{margin:.2em 0 .6em;font-size:.95em;}',
            '.am-node{display:flex;align-items:center;gap:.5em;padding:.3em 0;border-top:1px solid rgba(255,255,255,.05);}',
            '.am-node .am-name{flex:1;min-width:0;overflow-wrap:anywhere;}',
            '.am-node .am-size{opacity:.6;font-size:.9em;white-space:nowrap;font-variant-numeric:tabular-nums;}',
            '.am-node button{background:none;border:1px solid rgba(255,255,255,.2);color:inherit;border-radius:.3em;padding:.15em .55em;font:inherit;font-size:.8em;cursor:pointer;}',
            '.am-node button:hover,.am-node button:focus-visible{border-color:#00a4dc;outline:none;}',
            '.am-node button:disabled{opacity:.4;cursor:default;}',
            '.am-caret{background:none!important;border:none!important;width:1.4em;padding:0!important;font-size:1em!important;opacity:.75;}',
            '.am-caret[aria-expanded="true"]{transform:rotate(90deg);}',
            '.am-spacer{display:inline-block;width:1.4em;flex:none;}',
            '.am-kind{font-size:.7em;letter-spacing:.04em;text-transform:uppercase;opacity:.55;border:1px solid rgba(255,255,255,.2);border-radius:.25em;padding:0 .3em;flex:none;}',
            '.am-removed .am-name{opacity:.7;font-style:italic;}',
            '.am-leftout{opacity:.5;}',
            '.am-leftout .am-name{text-decoration:line-through;text-decoration-color:rgba(255,255,255,.35);}',
            '.am-leftout .am-name small{display:inline-block;text-decoration:none;}',
            '.am-nodl{width:4.6em;}',
            '.am-open-link{color:inherit;border:1px solid rgba(0,164,220,.6);border-radius:.3em;padding:.15em .55em;font-size:.8em;text-decoration:none;white-space:nowrap;}',
            '.am-open-link:hover,.am-open-link:focus-visible{border-color:#00a4dc;background:rgba(0,164,220,.15);outline:none;}',
            '.am-redirect .am-name{opacity:.85;}',
            '.am-foot{display:flex;justify-content:flex-end;gap:.6em;padding:.8em 1.2em 1em;border-top:1px solid rgba(255,255,255,.08);}',
            '.am-foot button{background:#00a4dc;border:none;color:#fff;border-radius:.3em;padding:.5em 1em;font:inherit;cursor:pointer;}',
            '.am-foot button:disabled{opacity:.4;cursor:default;}',
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

    function caret(expanded, label) {
        var c = el('button', 'am-caret', '\u25B8');
        c.type = 'button';
        c.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        c.setAttribute('aria-label', label);
        return c;
    }

    function kindOf(name) {
        var dot = name.lastIndexOf('.');
        return dot > 0 && dot > name.length - 7 ? name.slice(dot + 1).toUpperCase() : '';
    }

    // Folders are implied by the entries' paths; nested zips carry their own Children.
    function buildTree(entries, prefix) {
        var root = { folders: {}, order: [], files: [] };
        entries.forEach(function (e) {
            var parts = e.Path.split('/');
            var node = root;
            for (var i = 0; i < parts.length - 1; i++) {
                if (!node.folders[parts[i]]) {
                    node.folders[parts[i]] = { folders: {}, order: [], files: [], name: parts[i] };
                    node.order.push(parts[i]);
                }
                node = node.folders[parts[i]];
            }
            node.files.push({ name: parts[parts.length - 1], entry: e, path: prefix + e.Path });
        });
        return root;
    }

    function countFiles(node) {
        return node.files.length + node.order.reduce(function (n, k) { return n + countFiles(node.folders[k]); }, 0);
    }

    function renderNode(client, itemId, canDownload, node, depth, container, open) {
        var pad = (depth * 1.2) + 'em';
        node.order.forEach(function (key) {
            var folder = node.folders[key];
            var r = el('div', 'am-node am-folder');
            r.style.paddingLeft = pad;
            var c = caret(open, folder.name);
            r.appendChild(c);
            r.appendChild(el('span', 'am-name', folder.name + '/'));
            var n = countFiles(folder);
            r.appendChild(el('span', 'am-size', t(n === 1 ? 'dialog.file' : 'dialog.files', { count: n })));
            container.appendChild(r);
            var kids = el('div', 'am-kids');
            kids.hidden = !open;
            renderNode(client, itemId, canDownload, folder, depth + 1, kids, false);
            container.appendChild(kids);
            c.addEventListener('click', function () {
                kids.hidden = !kids.hidden;
                c.setAttribute('aria-expanded', kids.hidden ? 'false' : 'true');
            });
        });
        node.files.forEach(function (f) {
            var removed = /\.REMOVED\.txt$/i.test(f.name);
            var leftOut = !!f.entry.LeftOut;
            var link = leftOut && /^https?:\/\//i.test(f.entry.Link || '') ? f.entry.Link : null;
            var nested = f.entry.Children;
            var r = el('div', 'am-node am-file' + (removed ? ' am-removed' : '') + (link ? ' am-redirect' : leftOut ? ' am-leftout' : ''));
            r.style.paddingLeft = pad;
            var c = null;
            if (nested) {
                c = caret(false, f.name);
                r.appendChild(c);
            } else {
                r.appendChild(el('span', 'am-spacer'));
            }
            var shown = removed ? f.name.replace(/\.REMOVED\.txt$/i, '') : f.name;
            var kind = kindOf(shown);
            if (kind) {
                r.appendChild(el('span', 'am-kind', kind));
            }
            var name = el('span', 'am-name', shown);
            if (link) {
                // A placeholder that only sends the browser to a website: offer that website instead.
                var host = link.replace(/^https?:\/\//i, '').split(/[\/?#]/)[0];
                name.title = link;
                name.appendChild(el('small', 'am-note', ' \u2014 ' + t('dialog.opensSite', { host: host })));
            } else if (leftOut) {
                name.title = f.entry.Reason || '';
                name.appendChild(el('small', 'am-note', ' \u2014 ' + t('dialog.leftOut', { reason: f.entry.Reason || '' })));
            } else if (removed) {
                name.title = f.entry.Reason || t('dialog.removed');
                name.appendChild(el('small', 'am-note', ' \u2014 ' + t('dialog.removed')));
            } else if (f.entry.TooLargeToList) {
                name.appendChild(el('small', 'am-note', ' \u2014 ' + t('dialog.nestedTooLarge')));
            }
            r.appendChild(name);
            r.appendChild(el('span', 'am-size', formatSize(f.entry.Size)));
            if (link) {
                var a = el('a', 'am-open-link', t('dialog.openLink'));
                a.href = link;
                a.target = '_blank';
                a.rel = 'noopener noreferrer';
                a.title = link;
                r.appendChild(a);
                container.appendChild(r);
                return;
            }
            if (leftOut) {
                r.appendChild(el('span', 'am-spacer am-nodl'));
                container.appendChild(r);
                return;
            }
            var dl = el('button', 'am-file-download', t('dialog.download'));
            dl.type = 'button';
            dl.disabled = !canDownload;
            dl.title = t('dialog.downloadFile', { name: removed ? f.name : shown });
            dl.setAttribute('aria-label', dl.title);
            dl.addEventListener('click', function () { download(client, itemId, dl, f.path); });
            r.appendChild(dl);
            container.appendChild(r);
            if (nested) {
                var kids = el('div', 'am-kids');
                kids.hidden = true;
                renderNode(client, itemId, canDownload, buildTree(nested, f.path + '!/'), depth + 1, kids, true);
                container.appendChild(kids);
                c.addEventListener('click', function () {
                    kids.hidden = !kids.hidden;
                    c.setAttribute('aria-expanded', kids.hidden ? 'false' : 'true');
                });
            }
        });
    }

    // Fills container with an archive's file tree, fetched when first shown.
    function showTree(client, itemId, container) {
        if (container.dataset.loaded) {
            return;
        }
        container.dataset.loaded = '1';
        container.appendChild(el('p', 'am-note', t('dialog.loading')));
        contents(client, itemId).then(function (c) {
            container.textContent = '';
            if (!c.Entries.length) {
                container.appendChild(el('p', 'am-note', t('dialog.emptyArchive')));
                return;
            }
            var root = buildTree(c.Entries, '');
            renderNode(client, itemId, c.CanDownload, root, 0, container, c.Entries.length <= 40);
            if (c.Truncated) {
                container.appendChild(el('p', 'am-note', t('dialog.truncated', { count: c.Entries.length })));
            }
        }).catch(function () {
            container.textContent = '';
            delete container.dataset.loaded;
            container.appendChild(el('p', 'am-note', t('dialog.loadFailed')));
        });
    }

    function row(client, tr, item, label, gotoLabel) {
        var block = el('div', 'am-archive');
        var r = el('div', 'am-row');
        var treeBox = el('div', 'am-tree');
        treeBox.hidden = true;
        var c = caret(false, t('dialog.contents'));
        c.classList.add('am-contents-toggle');
        c.addEventListener('click', function () {
            treeBox.hidden = !treeBox.hidden;
            c.setAttribute('aria-expanded', treeBox.hidden ? 'false' : 'true');
            if (!treeBox.hidden) {
                showTree(client, item.ItemId, treeBox);
            }
        });
        r.appendChild(c);
        r.appendChild(el('span', 'am-n', item.IndexNumber !== null && item.IndexNumber !== undefined && item.Level === 'lesson' ? String(item.IndexNumber) : ''));
        r.appendChild(el('span', 'am-name', label));
        r.appendChild(el('span', 'am-size', formatOf(item.FileName) + ' ' + formatSize(item.Size)));
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
        block.appendChild(r);
        block.appendChild(treeBox);
        return block;
    }

    function openDialog(client, tr) {
        closeDialog();
        injectStyles();
        var single = tr.Self && tr.Groups.length === 0;   // one archive: show its files straight away
        var backdrop = el('div', 'am-backdrop');
        var dialog = el('div', 'am-dialog');
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        var head = el('div', 'am-head');
        head.appendChild(iconElement('am-icon'));
        var title = el('div', 'am-title', single ? t('dialog.contents') : t('dialog.title'));
        title.appendChild(el('small', '', tr.Name));
        head.appendChild(title);
        var close = el('button', 'am-close', '\u00D7');
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
        if (single) {
            var only = el('div', 'am-tree am-single');
            body.appendChild(only);
            showTree(client, tr.Self.ItemId, only);
        } else {
            if (tr.Self) {
                var own = el('div', 'am-group');
                own.appendChild(el('h3', '', t('dialog.this.' + tr.Type) !== 'dialog.this.' + tr.Type ? t('dialog.this.' + tr.Type) : t('dialog.this.other')));
                own.appendChild(row(client, tr, tr.Self, tr.Self.FileName, null));
                body.appendChild(own);
            }
            tr.Groups.forEach(function (g) {
                var group = el('div', 'am-group');
                group.appendChild(el('h3', '', (g.IndexNumber !== null && g.IndexNumber !== undefined ? g.IndexNumber + ' \u00B7 ' : '') + (g.Name || '')));
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
        }
        dialog.appendChild(body);
        if (tr.Self) {
            // The item's own archive, downloadable from the foot of the picker: everything when it is
            // the only archive, otherwise this level's (the course's or section's) archive.
            var foot = el('div', 'am-foot');
            var one = !/\.zip$/i.test(tr.Self.FileName || '');
            var levelKey = 'dialog.downloadLevel.' + tr.Type;
            var all = el('button', 'am-download-all', one
                ? t('dialog.downloadOne', { name: tr.Self.FileName, size: formatSize(tr.Self.Size) })
                : single
                    ? t('dialog.downloadAll', { size: formatSize(tr.Self.Size) })
                    : t(t(levelKey) !== levelKey ? levelKey : 'dialog.downloadLevel.other', { format: formatOf(tr.Self.FileName), size: formatSize(tr.Self.Size) }));
            all.type = 'button';
            all.disabled = !tr.CanDownload;
            all.addEventListener('click', function () { download(client, tr.Self.ItemId, all); });
            foot.appendChild(all);
            dialog.appendChild(foot);
        }
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
    // One button on every page: it opens the picker, which downloads from there.
    function makeButton(client, itemId, tr) {
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'emby-button button-flat detailButton ' + BUTTON_CLASS;
        button.dataset.itemId = itemId;
        var count = countOf(tr);
        var label;
        if (tr.Self && tr.Groups.length === 0) {
            label = t('button.contentsOf', { format: formatOf(tr.Self.FileName), size: formatSize(tr.Self.Size) });
        } else {
            label = t(count === 1 ? 'button.countOne' : 'button.count', { count: count });
        }
        button.title = label;
        button.setAttribute('aria-label', label);
        var content = el('div', 'detailButton-content');
        content.appendChild(iconElement('detailButton-icon additionalMaterialIcon'));
        button.appendChild(content);
        button.addEventListener('click', function () { activate(client, itemId); });
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

    // Idempotent, and run on every pass: Jellyfin re-renders cards (React re-builds a card's
    // indicator row when its data refreshes, e.g. on returning to the tab) and recycles elements
    // for other items, so "already done" is judged by whether the right icon is there now.
    function decorate(client, elem, kind, status) {
        var id = normId(elem.getAttribute('data-id'));
        var existing = elem.querySelector(kind === 'card' ? '.additionalMaterialIndicator' : '.additionalMaterialListButton');
        if (existing && existing.dataset.amItem === id && status) {
            return;
        }
        if (existing) {
            existing.remove();   // left over from the item this element showed before
        }
        if (!status) {  // false: settled, no material
            return;
        }
        var n = (status.Own ? 1 : 0) + status.Below;
        var label = status.Own && status.Below === 0 ? t('dialog.title') : t(n === 1 ? 'button.countOne' : 'button.count', { count: n });
        var handler = function (e) {
            e.preventDefault();
            e.stopPropagation();
            activate(client, id);
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
            b.dataset.amItem = id;
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
        var pending = targets;
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
    window.setInterval(function () { treeCache = {}; contentsCache = {}; statusCache = {}; }, 120000);
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
    // The dialog belongs to the page it was opened on: leaving the page (a link, Back) closes it.
    window.addEventListener('hashchange', closeDialog);
    window.addEventListener('popstate', closeDialog);
    schedule();
})();
