/* Shared helpers for YARP UI pages. */
(function () {
    'use strict';

    var ICONS = {
        route: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h4l3-6 4 12 3-6h4"/></svg>',
        cluster: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l7.5 4.3v9.4L12 21l-7.5-4.3V7.3z"/><path d="M12 12l7.5-4.3M12 12L4.5 7.7M12 12v9"/></svg>',
        dest: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="7" rx="2"/><rect x="4" y="13" width="16" height="7" rx="2"/><path d="M8 7.5h.01M8 16.5h.01"/></svg>',
        plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
        minus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M5 12h14"/></svg>',
        fit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9V6a2 2 0 0 1 2-2h3M15 4h3a2 2 0 0 1 2 2v3M20 15v3a2 2 0 0 1-2 2h-3M9 20H6a2 2 0 0 1-2-2v-3"/></svg>',
        refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/></svg>',
        close: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg>',
        trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h16"/><path d="M10 11v6M14 11v6"/><path d="M6 7l1 13h10l1-13"/><path d="M9 7V4h6v3"/></svg>',
        search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/></svg>',
        edit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20h4L19 9a2.5 2.5 0 0 0-3.5-3.5L4.5 16.5z"/></svg>',
        warn: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 22 21H2Z"/><path d="M12 10v5"/><path d="M12 18.2h.01"/></svg>',
        lock: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
        save: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4h11l3 3v13H5z"/><path d="M8 4v5h7V4"/><path d="M8 20v-6h8v6"/></svg>',
        info: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M12 11v5"/><path d="M12 8h.01"/></svg>',
        link: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M10 14a5 5 0 0 0 7.07 0l2.83-2.83a5 5 0 0 0-7.07-7.07L11 5.9"/><path d="M14 10a5 5 0 0 0-7.07 0L4.1 12.83a5 5 0 0 0 7.07 7.07L13 18.1"/></svg>'
    };

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    async function api(url, options) {
        var opts = Object.assign({ method: 'GET' }, options || {});
        var res = await fetch(url, opts);
        if (res.status === 401) {
            window.location.href = '/login?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
            throw new Error('Not signed in');
        }
        return res;
    }

    function toast(message, type) {
        type = type || 'info';
        var stack = document.getElementById('toast-stack');
        if (!stack) { return; }
        var el = document.createElement('div');
        el.className = 'toast toast-' + type;
        el.innerHTML = (type === 'success' || type === 'error' ? ICONS.warn : ICONS.info) + '<span>' + esc(message) + '</span>';
        el.addEventListener('click', function () { dismiss(); });
        stack.appendChild(el);
        requestAnimationFrame(function () { el.classList.add('show'); });
        var timer = setTimeout(dismiss, 4200);
        function dismiss() {
            clearTimeout(timer);
            el.classList.remove('show');
            el.classList.add('hide');
            setTimeout(function () { el.remove(); }, 350);
        }
    }

    // JSON syntax tinting for the detail drawer.
    function prettyJson(value) {
        var json = JSON.stringify(value, null, 2) ?? '';
        var html = esc(json);
        html = html.replace(/&quot;(\\u[a-fA-F0-9]{4}|\\[^u]|[^\\&quot;])*?&quot;(\s*:)?|\b(true|false)\b|\bnull\b|-?\d+(\.\d+)?([eE][+-]?\d+)?/g, function (match) {
            var cls = 'j-num';
            if (/^&quot;/.test(match)) {
                cls = /:$/.test(match) ? 'j-key' : 'j-str';
            } else if (/true|false/.test(match)) {
                cls = 'j-bool';
            } else if (/null/.test(match)) {
                cls = 'j-null';
            }
            return '<span class="' + cls + '">' + match + '</span>';
        });
        return html;
    }

    function debounce(fn, wait) {
        var timer = null;
        return function () {
            var args = arguments;
            var self = this;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(self, args); }, wait);
        };
    }

    // Shared "config source" pill (map + editor toolbars).
    function setSourcePill(managedByUi) {
        var pill = document.getElementById('source-pill');
        if (!pill) { return; }
        if (managedByUi) {
            pill.textContent = 'UI-managed config';
            pill.className = 'pill pill-accent';
            pill.title = 'Live configuration is persisted in yarp-ui.routes.json, which overrides the ReverseProxy section in appsettings.json.';
        } else {
            pill.textContent = 'appsettings.json';
            pill.className = 'pill';
            pill.title = 'Live configuration comes from the ReverseProxy section in appsettings.json. The first save from the UI switches to a managed file.';
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var page = document.body.dataset.page;
        document.querySelectorAll('#main-nav a').forEach(function (link) {
            if (link.dataset.nav === page) {
                link.classList.add('active');
            }
        });
    });

    window.YarpUi = {
        esc: esc,
        api: api,
        toast: toast,
        prettyJson: prettyJson,
        debounce: debounce,
        setSourcePill: setSourcePill,
        icon: function (name) { return ICONS[name] || ''; }
    };
})();
