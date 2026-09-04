/* Request logs: the table is a server-side paged query over /api/yarp/logs — the page loads
   only the latest PAGE_SIZE entries and walks history with limit/offset. All filters (time
   frame, route/cluster/destination, free text, status class) and column sorting run server-side
   over the whole retained window. Live updates keep the first page fresh: a cheap after-cursor
   poll prepends new entries (or refetches page 1 when sorted/filtered); deeper pages stay stable
   while browsing. The performance panel aggregates server-side: /api/yarp/logs/stats every 5s
   (stat cards, per-route bars, P95 line) and a per-request duration scatter fed from the loaded
   entries. */
(function () {
    'use strict';

    var esc = window.YarpUi.esc;
    var S = window.YarpUi.S;
    var Sn = window.YarpUi.Sn;

    var entries = [];
    var lastSeq = 0;
    var inFlight = false;
    var total = 0;         // server-side match count for the current query
    var page = 1;          // 1-based page into the server-side query
    var requestSeq = 0;    // ignores stale query responses (out-of-order fetches)

    var filterText = '';
    var filterStatus = '';
    var auto = true;

    // Server-side filters. range '' = latest page + live updates; 'all' | 'custom' | minutes = history query.
    var filters = { range: '', fromMs: null, toMs: null, routeId: '', clusterId: '', destinationId: '' };
    var sort = { field: 'timestamp', dir: 'desc' };

    var PAGE_SIZE = 10;

    /* ---- filter dropdown sources (/api/yarp/config is PascalCase; log entries are camelCase) ---- */

    var configRoutes = [];
    var configClusters = {}; // clusterId -> destination names
    var seenRoutes = {};     // ids that appeared in log rows but are no longer in the config
    var seenClusters = {};
    var seenDestinations = {};

    /* ---- performance panel state ---- */

    var stats = null;          // last /logs/stats response
    var statsWindow = 15;      // minutes; 0 = all time
    var statsInFlight = false;
    var chart = null;

    var STATUS_COLORS = {
        '2xx': '#86efac',
        '3xx': '#fcd34d',
        '4xx': '#fdba74',
        '5xx': '#fca5a5',
        'failed': '#8f9db4'
    };

    function statusClass(code) {
        if (code == null) { return 's-none'; }
        if (code >= 500) { return 's-5'; }
        if (code >= 400) { return 's-4'; }
        if (code >= 300) { return 's-3'; }
        if (code >= 200) { return 's-2'; }
        return 's-none';
    }

    function statusGroup(code) {
        if (code == null) { return 'failed'; }
        return code >= 500 ? '5xx' : code >= 400 ? '4xx' : code >= 300 ? '3xx' : code >= 200 ? '2xx' : 'failed';
    }

    function formatTime(iso) {
        var d = new Date(iso);
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(Math.floor(d.getMilliseconds() / 10));
    }

    function pad(n) { return (n < 10 ? '0' : '') + n; }

    /* ---- one-click IP blocking (client IP cells) ---- */

    function renderClientIp(ip) {
        if (!ip) { return '—'; }
        return '<span>' + esc(ip) + '</span>' +
            '<button type="button" class="btn btn-ghost btn-sm block-ip" data-ip="' + esc(ip) + '" title="' + esc(S('logs.blockIpTitle')) + '">' +
            '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">' +
            '<circle cx="12" cy="12" r="9" /><path d="M5.5 5.5l13 13" /></svg></button>';
    }

    async function blockIp(ip) {
        if (!window.confirm(S('logs.blockIpConfirm', ip))) { return; }
        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking/rules', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ value: ip, note: S('ipblocking.fromLogs') })
            });
            if (res.ok) {
                window.YarpUi.toast(S('logs.blockIpDone', ip), 'success');
            } else {
                var data = await res.json().catch(function () { return null; });
                window.YarpUi.toast(S('logs.blockIpFailed', ip, (data && data.errors && data.errors[0]) || res.status), 'error');
            }
        } catch (e) {
            window.YarpUi.toast(S('logs.blockIpFailed', ip, e.message), 'error');
        }
    }

    function fmtMs(value) {
        if (value == null || isNaN(value)) { return '—'; }
        if (value >= 1000) { return S('logs.fmtSeconds', (value / 1000).toFixed(2)); }
        if (value >= 100) { return S('logs.fmtMs', Math.round(value)); }
        return S('logs.fmtMs', value.toFixed(1));
    }

    function filtersActive() {
        return filters.range !== '' || filters.routeId !== '' || filters.clusterId !== '' || filters.destinationId !== '';
    }

    function anyFilterActive() {
        return filtersActive() || filterText !== '' || filterStatus !== '';
    }

    function applySortHeaders() {
        Array.prototype.forEach.call(document.querySelectorAll('.th-sortable'), function (th) {
            var active = th.getAttribute('data-sort') === sort.field;
            th.classList.toggle('sorted-asc', active && sort.dir === 'asc');
            th.classList.toggle('sorted-desc', active && sort.dir === 'desc');
            th.setAttribute('aria-sort', active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none');
        });
    }

    /* ---- table ---- */

    function render() {
        var rows = document.getElementById('log-rows');

        var countLabel = document.getElementById('log-count');
        var from = total === 0 ? 0 : (page - 1) * PAGE_SIZE + 1;
        countLabel.textContent = S('logs.countPage', from.toLocaleString(), ((page - 1) * PAGE_SIZE + entries.length).toLocaleString(), total.toLocaleString());

        var filtered = anyFilterActive();
        document.getElementById('log-empty').classList.toggle('hidden', entries.length !== 0);
        document.getElementById('log-empty-live').classList.toggle('hidden', filtered);
        document.getElementById('log-empty-hint').classList.toggle('hidden', filtered);
        document.getElementById('log-empty-filtered').classList.toggle('hidden', !filtered);

        if (!entries.length) {
            rows.innerHTML = '';
        } else {
            rows.innerHTML = entries.map(function (e) {
                var status = e.statusCode == null ? '—' : e.statusCode;
                var title = e.error ? ' title="' + esc(e.error) + '"' : '';
                return '<tr class="' + statusClass(e.statusCode) + '"' + title + '>' +
                    '<td class="col-time mono">' + formatTime(e.timestampUtc) + '</td>' +
                    '<td class="col-method"><span class="method-pill m-' + esc((e.method || '').toLowerCase()) + '">' + esc(e.method) + '</span></td>' +
                    '<td class="mono cell-path" title="' + esc(e.path) + '">' + esc(e.path) + '</td>' +
                    '<td class="col-status"><span class="status-pill ' + statusClass(e.statusCode) + '">' + status + '</span></td>' +
                    '<td class="col-duration mono">' + (e.durationMs == null ? '—' : S('logs.fmtMs', e.durationMs.toFixed(1))) + '</td>' +
                    '<td class="mono cell-dim cell-clientip">' + renderClientIp(e.clientIp) + '</td>' +
                    '<td class="mono cell-dim">' + esc(e.routeId || '—') + '</td>' +
                    '<td class="mono cell-dim">' + esc(e.clusterId || '—') + '</td>' +
                    '<td class="mono cell-dim" title="' + esc(e.destinationAddress || '') + '">' + esc(e.destinationId || '—') + '</td>' +
                    '</tr>';
            }).join('');
        }

        trackSeenIds();
        applySortHeaders();
        renderPager();
        renderChart();
    }

    /* ---- pager ---- */

    function pageCount() {
        return Math.max(1, Math.ceil(total / PAGE_SIZE));
    }

    function renderPager() {
        var pages = pageCount();
        document.getElementById('log-page-label').textContent = S('logs.pageLabel', page.toLocaleString(), pages.toLocaleString());
        document.getElementById('log-prev').disabled = page <= 1;
        document.getElementById('log-next').disabled = page >= pages;
    }

    function goToPage(target) {
        var pages = pageCount();
        var next = Math.min(Math.max(1, target), pages);
        if (next === page) { return; }
        page = next;
        queryLogs();
        // Keep the table in view when jumping between pages of a long table.
        var table = document.querySelector('.table-wrap');
        if (table) { table.scrollTop = 0; }
    }

    async function poll() {
        if (inFlight) { return; }
        inFlight = true;
        try {
            if (anyFilterActive()) {
                await queryLogs();
            } else {
                await pollLive();
            }
        } catch (e) {
            /* transient — retried on the next tick */
        } finally {
            inFlight = false;
        }
    }

    // Cheap delta poll (after-cursor). On the unsorted first page new entries are prepended
    // locally; a sorted/filtered first page is refetched instead. Deeper pages stay stable so
    // rows don't shift underneath while browsing history.
    async function pollLive() {
        var res = await window.YarpUi.api('/api/yarp/logs?after=' + lastSeq);
        if (!res.ok) { return; }
        var data = await res.json();
        var fresh = data.entries || [];
        if (!fresh.length) { return; }
        trackSeq(fresh);
        if (page !== 1) { return; }
        if (sort.field === 'timestamp' && sort.dir === 'desc') {
            var merged = fresh.concat(entries);
            entries = merged.slice(Math.max(0, merged.length - PAGE_SIZE));
            total += fresh.length;
            render();
        } else {
            await queryLogs();
        }
    }

    // Paged history query (server-side filters + sort). Preset ranges recompute their start on
    // every call so a rolling "last N minutes" stays current.
    async function queryLogs() {
        computeRange();
        var id = ++requestSeq;
        var params = new URLSearchParams();
        params.set('sort', sort.field);
        params.set('desc', sort.dir === 'desc');
        params.set('limit', String(PAGE_SIZE));
        params.set('offset', String((page - 1) * PAGE_SIZE));
        if (filters.fromMs !== null) { params.set('from', String(filters.fromMs)); }
        if (filters.toMs !== null) { params.set('to', String(filters.toMs)); }
        if (filters.routeId) { params.set('routeId', filters.routeId); }
        if (filters.clusterId) { params.set('clusterId', filters.clusterId); }
        if (filters.destinationId) { params.set('destinationId', filters.destinationId); }
        if (filterText) { params.set('q', filterText); }
        if (filterStatus) { params.set('status', filterStatus); }

        var res = await window.YarpUi.api('/api/yarp/logs?' + params.toString());
        if (!res.ok || id !== requestSeq) { return; }
        var data = await res.json();
        total = typeof data.total === 'number' ? data.total : 0;
        entries = data.entries || [];
        trackSeq(entries);
        render();
    }

    function computeRange() {
        if (filters.range === 'custom') {
            filters.fromMs = dateInputMs('log-from');
            filters.toMs = dateInputMs('log-to');
        } else if (filters.range === 'all') {
            filters.fromMs = null;
            filters.toMs = null;
        } else if (filters.range) {
            filters.fromMs = Date.now() - (+filters.range) * 60000;
            filters.toMs = null;
        } else {
            filters.fromMs = null;
            filters.toMs = null;
        }
    }

    function dateInputMs(id) {
        var value = document.getElementById(id).value;
        if (!value) { return null; }
        var ms = Date.parse(value); // datetime-local has no zone — parsed in the browser's local time
        return isNaN(ms) ? null : ms;
    }

    function trackSeq(list) {
        for (var i = 0; i < list.length; i++) {
            if (list[i].seq > lastSeq) { lastSeq = list[i].seq; }
        }
    }

    /* ---- duration chart (per-request scatter from the polled entries) ---- */

    function windowStartMs() {
        return statsWindow > 0 ? Date.now() - statsWindow * 60000 : 0;
    }

    function initChart() {
        if (typeof Chart === 'undefined') { return; } // lib failed to load — panel degrades to stats only

        var canvas = document.getElementById('logs-chart');
        if (!canvas) { return; }

        var groups = Object.keys(STATUS_COLORS);
        var datasets = groups.map(function (group) {
            return {
                label: group === 'failed' ? S('logs.chartFailed') : group,
                data: [],
                backgroundColor: STATUS_COLORS[group],
                pointRadius: 2.5,
                pointHoverRadius: 5,
                pointStyle: 'circle'
            };
        });
        datasets.push({
            label: 'P95',
            data: [],
            type: 'line',
            showLine: true,
            borderColor: '#22d3ee',
            borderDash: [6, 4],
            borderWidth: 1.5,
            pointRadius: 0,
            pointHitRadius: 0,
            fill: false
        });

        chart = new Chart(canvas.getContext('2d'), {
            type: 'scatter',
            data: { datasets: datasets },
            options: {
                animation: false,
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'nearest', intersect: false },
                scales: {
                    x: {
                        type: 'linear',
                        ticks: {
                            maxTicksLimit: 8,
                            callback: function (value) {
                                var d = new Date(value);
                                return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
                            }
                        },
                        grid: { color: 'rgba(148, 163, 184, 0.08)' }
                    },
                    y: {
                        beginAtZero: true,
                        ticks: { callback: function (value) { return S('logs.fmtMs', value); } },
                        grid: { color: 'rgba(148, 163, 184, 0.08)' }
                    }
                },
                plugins: {
                    legend: {
                        labels: { usePointStyle: true, boxWidth: 7, color: '#8f9db4', font: { size: 11 } }
                    },
                    tooltip: {
                        callbacks: {
                            label: function (item) {
                                if (item.dataset.label === 'P95') {
                                    return ' ' + S('logs.tooltipP95', fmtMs(item.parsed.y));
                                }
                                var entry = item.raw.entry;
                                if (!entry) { return ' ' + fmtMs(item.parsed.y); }
                                return ' ' + formatTime(entry.timestampUtc) + ' · ' + (entry.statusCode == null ? S('logs.chartFailed') : entry.statusCode) +
                                    ' · ' + fmtMs(entry.durationMs) + ' · ' + (entry.routeId || S('logs.noRoute'));
                            }
                        }
                    }
                }
            }
        });
    }

    function renderChart() {
        if (!chart) { return; }

        var start = windowStartMs();
        var byGroup = {};
        Object.keys(STATUS_COLORS).forEach(function (group) { byGroup[group] = []; });

        entries.forEach(function (e) {
            var x = new Date(e.timestampUtc).getTime();
            if (x < start) { return; }
            byGroup[statusGroup(e.statusCode)].push({ x: x, y: e.durationMs, entry: e });
        });

        Object.keys(STATUS_COLORS).forEach(function (group, i) {
            chart.data.datasets[i].data = byGroup[group];
        });

        // P95 reference line from the stats endpoint, spanning the plotted range.
        var p95 = stats && stats.summary && stats.summary.count > 0 ? stats.summary.p95Ms : null;
        var line = [];
        if (p95 != null) {
            var xs = byGroup['2xx'].concat(byGroup['3xx'], byGroup['4xx'], byGroup['5xx'], byGroup['failed'])
                .map(function (p) { return p.x; });
            var minX = xs.length ? Math.min.apply(null, xs) : (statsWindow > 0 ? Date.now() - statsWindow * 60000 : Date.now() - 900000);
            var maxX = xs.length ? Math.max.apply(null, xs) : Date.now();
            line = [{ x: minX, y: p95 }, { x: maxX, y: p95 }];
        }
        chart.data.datasets[Object.keys(STATUS_COLORS).length].data = line;

        chart.update('none');
    }

    /* ---- stats (server-side aggregates over the whole database window) ---- */

    async function fetchStats() {
        if (statsInFlight) { return; }
        statsInFlight = true;
        try {
            var res = await window.YarpUi.api('/api/yarp/logs/stats?minutes=' + statsWindow);
            if (res.ok) {
                stats = await res.json();
                renderStats();
                renderChart();
            }
        } catch (e) {
            /* transient — retried on the next tick */
        } finally {
            statsInFlight = false;
        }
    }

    function renderStats() {
        if (!stats || !stats.summary) { return; }
        var s = stats.summary;

        document.getElementById('stat-count').textContent = s.count.toLocaleString();
        document.getElementById('stat-avg').textContent = fmtMs(s.avgMs);
        document.getElementById('stat-p95').textContent = fmtMs(s.p95Ms);
        document.getElementById('stat-max').textContent = fmtMs(s.maxMs);

        var errors = document.getElementById('stat-errors');
        var rate = s.count > 0 ? (s.errorCount / s.count) * 100 : 0;
        errors.textContent = s.errorCount.toLocaleString() + ' · ' + rate.toFixed(1) + '%';
        errors.classList.toggle('stat-danger', s.errorCount > 0);

        var windowText = statsWindow > 0
            ? (statsWindow >= 60 ? S('logs.captionHour', statsWindow / 60) : S('logs.captionMin', statsWindow))
            : S('logs.captionAll');
        var updated = pad(new Date().getHours()) + ':' + pad(new Date().getMinutes()) + ':' + pad(new Date().getSeconds());
        document.getElementById('stats-caption').textContent = S('logs.captionUpdated', windowText, updated);

        renderRouteBars(stats.routes || []);
    }

    function renderRouteBars(routes) {
        var list = document.getElementById('route-bars-list');
        if (!routes.length) {
            list.innerHTML = '<div class="muted small route-bars-empty">' + esc(S('logs.noRequestsYet')) + '</div>';
            return;
        }

        var top = routes.slice(0, 8);
        var maxP95 = Math.max.apply(null, top.map(function (r) { return r.p95Ms; })) || 1;

        list.innerHTML = top.map(function (r) {
            var width = Math.max(2, Math.round((r.p95Ms / maxP95) * 100));
            var meta = S('logs.routeBarMeta', r.count.toLocaleString(), fmtMs(r.avgMs), fmtMs(r.maxMs));
            if (r.errorCount > 0) { meta += ' · <span class="route-bar-errors">' + esc(Sn('logs.errorCount', r.errorCount)) + '</span>'; }
            return '<div class="route-bar-row">' +
                '<div class="route-bar-top"><span class="mono" title="' + esc(r.routeId || S('logs.noRoute')) + '">' + esc(r.routeId || S('logs.noRoute')) + '</span>' +
                '<span class="muted small">' + meta + '</span></div>' +
                '<div class="route-bar-track"><div class="route-bar-fill" style="width:' + width + '%"></div></div>' +
                '</div>';
        }).join('');
    }

    /* ---- route/cluster/destination filter dropdowns ---- */

    async function loadFilterOptions() {
        try {
            var res = await window.YarpUi.api('/api/yarp/config');
            if (!res.ok) { return; }
            var cfg = await res.json();
            configRoutes = (cfg.Routes || []).map(function (r) { return r.RouteId; });
            configClusters = {};
            (cfg.Clusters || []).forEach(function (c) {
                configClusters[c.ClusterId] = Object.keys(c.Destinations || {});
            });
            rebuildFilterOptions();
        } catch (e) {
            /* dropdowns stay sparse — ids seen in captured entries still become selectable */
        }
    }

    // Options come from the live config plus ids observed in log rows (routes/clusters can be
    // deleted while their history is still retained). Destination options scope to the selected
    // cluster, or span every cluster when none is selected.
    function rebuildFilterOptions() {
        rebuildSelect('log-route', S('logs.allRoutes'),
            uniqueSorted(configRoutes.concat(Object.keys(seenRoutes))), filters.routeId);
        rebuildSelect('log-cluster', S('logs.allClusters'),
            uniqueSorted(Object.keys(configClusters).concat(Object.keys(seenClusters))), filters.clusterId);

        var destinations = [];
        if (filters.clusterId && configClusters[filters.clusterId]) {
            destinations = configClusters[filters.clusterId].slice();
        } else {
            Object.keys(configClusters).forEach(function (id) {
                destinations = destinations.concat(configClusters[id]);
            });
        }
        rebuildSelect('log-destination', S('logs.allDestinations'),
            uniqueSorted(destinations.concat(Object.keys(seenDestinations))), filters.destinationId);
    }

    function trackSeenIds() {
        var added = false;
        entries.forEach(function (e) {
            if (e.routeId && !seenRoutes[e.routeId]) { seenRoutes[e.routeId] = true; added = true; }
            if (e.clusterId && !seenClusters[e.clusterId]) { seenClusters[e.clusterId] = true; added = true; }
            if (e.destinationId && !seenDestinations[e.destinationId]) { seenDestinations[e.destinationId] = true; added = true; }
        });
        if (added) { rebuildFilterOptions(); }
    }

    function uniqueSorted(values) {
        var seen = {};
        var list = [];
        values.forEach(function (v) {
            if (v && !seen[v]) { seen[v] = true; list.push(v); }
        });
        return list.sort();
    }

    function rebuildSelect(id, allLabel, values, selected) {
        var select = document.getElementById(id);
        if (!select) { return; }
        var current = selected || '';
        select.innerHTML = '<option value="">' + esc(allLabel) + '</option>' +
            values.map(function (v) {
                return '<option value="' + esc(v) + '"' + (v === current ? ' selected' : '') + '>' + esc(v) + '</option>';
            }).join('');
        select.value = values.indexOf(current) !== -1 ? current : '';
    }

    /* ---- retention policy ---- */

    function retentionLabel(days) {
        if (days === 0) { return S('logs.keepForever'); }
        if (days === 1) { return S('logs.keepOneDay'); }
        if (days === 365) { return S('logs.keepOneYear'); }
        return S('logs.keepDays', days);
    }

    function setRetentionValue(days) {
        var select = document.getElementById('log-retention');
        var exists = Array.prototype.some.call(select.options, function (o) { return +o.value === days; });
        if (!exists) {
            var option = document.createElement('option');
            option.value = String(days);
            option.textContent = retentionLabel(days);
            select.appendChild(option);
        }
        select.value = String(days);
    }

    async function loadRetention() {
        try {
            var res = await window.YarpUi.api('/api/yarp/logs/settings');
            if (res.ok) {
                var settings = await res.json();
                setRetentionValue(settings.retentionDays);
            }
        } catch (e) {
            /* transient — the select just keeps its defaults */
        }
    }

    async function saveRetention(days) {
        try {
            var res = await window.YarpUi.api('/api/yarp/logs/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ retentionDays: days })
            });
            if (res.ok) {
                window.YarpUi.toast(days === 0 ? S('logs.keepForeverToast') : Sn('logs.olderThan', days), 'success');
            } else {
                var data = await res.json().catch(function () { return null; });
                window.YarpUi.toast(S('logs.retentionSaveFailed', (data && data.errors && data.errors[0]) || res.status), 'error');
                loadRetention(); // snap the select back to the stored value
            }
        } catch (e) {
            window.YarpUi.toast(S('logs.retentionSaveFailed', e.message), 'error');
        }
    }

    /* ---- panel collapse ---- */

    function applyChartsVisible(visible) {
        document.getElementById('charts-body').classList.toggle('hidden', !visible);
        document.getElementById('charts-toggle').classList.toggle('collapsed', !visible);
        document.getElementById('charts-toggle').setAttribute('aria-expanded', String(visible));
        try { window.localStorage.setItem('yarpui.logs.chartsVisible', visible ? '1' : '0'); } catch (e) { /* private mode */ }
    }

    function applyFiltersChanged() {
        document.getElementById('log-reset').classList.toggle('hidden', !anyFilterActive());
        document.getElementById('log-from').classList.toggle('hidden', filters.range !== 'custom');
        document.getElementById('log-to').classList.toggle('hidden', filters.range !== 'custom');
        page = 1;
        queryLogs();
    }

    /* ---- wiring ---- */

    document.addEventListener('DOMContentLoaded', function () {
        // Free text and status-class filters run server-side over the whole retained window.
        document.getElementById('log-search').addEventListener('input', window.YarpUi.debounce(function (e) {
            filterText = e.target.value.trim();
            page = 1;
            queryLogs();
        }, 250));

        document.getElementById('log-status').addEventListener('change', function (e) {
            filterStatus = e.target.value;
            page = 1;
            queryLogs();
        });

        var autoToggle = document.getElementById('log-auto');
        autoToggle.addEventListener('change', function (e) {
            auto = e.target.checked;
            document.getElementById('live-dot').classList.toggle('on', auto);
        });
        document.getElementById('live-dot').classList.add('on');

        document.getElementById('log-route').addEventListener('change', function (e) {
            filters.routeId = e.target.value;
            applyFiltersChanged();
        });

        document.getElementById('log-cluster').addEventListener('change', function (e) {
            filters.clusterId = e.target.value;
            filters.destinationId = ''; // destination options are scoped to the cluster
            rebuildFilterOptions();
            applyFiltersChanged();
        });

        document.getElementById('log-destination').addEventListener('change', function (e) {
            filters.destinationId = e.target.value;
            applyFiltersChanged();
        });

        document.getElementById('log-range').addEventListener('change', function (e) {
            filters.range = e.target.value;
            applyFiltersChanged();
        });

        ['log-from', 'log-to'].forEach(function (id) {
            document.getElementById(id).addEventListener('change', function () {
                if (filters.range === 'custom') { applyFiltersChanged(); }
            });
        });

        document.getElementById('log-reset').addEventListener('click', function () {
            filters = { range: '', fromMs: null, toMs: null, routeId: '', clusterId: '', destinationId: '' };
            filterText = '';
            filterStatus = '';
            ['log-search', 'log-status', 'log-route', 'log-cluster', 'log-destination', 'log-range', 'log-from', 'log-to'].forEach(function (id) {
                document.getElementById(id).value = '';
            });
            rebuildFilterOptions();
            applyFiltersChanged();
        });

        // Column sorting is server-side — the current page is re-queried under the new order.
        Array.prototype.forEach.call(document.querySelectorAll('.th-sortable'), function (th) {
            th.addEventListener('click', function () {
                var field = th.getAttribute('data-sort');
                if (sort.field === field) {
                    sort.dir = sort.dir === 'asc' ? 'desc' : 'asc';
                } else {
                    sort.field = field;
                    sort.dir = 'desc';
                }
                page = 1;
                queryLogs();
            });
        });

        document.getElementById('log-prev').addEventListener('click', function () { goToPage(page - 1); });
        document.getElementById('log-next').addEventListener('click', function () { goToPage(page + 1); });

        document.getElementById('log-rows').addEventListener('click', function (e) {
            var button = e.target.closest ? e.target.closest('.block-ip') : null;
            if (button) { blockIp(button.getAttribute('data-ip')); }
        });

        document.getElementById('stats-window').addEventListener('change', function (e) {
            statsWindow = +e.target.value;
            fetchStats();
        });

        document.getElementById('log-retention').addEventListener('change', function (e) {
            saveRetention(+e.target.value);
        });

        document.getElementById('charts-toggle').addEventListener('click', function () {
            applyChartsVisible(document.getElementById('charts-body').classList.contains('hidden'));
        });

        document.getElementById('log-clear').addEventListener('click', async function () {
            try {
                await window.YarpUi.api('/api/yarp/logs', { method: 'DELETE' });
                entries = [];
                lastSeq = 0;
                total = 0;
                page = 1;
                render();
                queryLogs();
                fetchStats();
                window.YarpUi.toast(S('logs.cleared'), 'success');
            } catch (e) {
                window.YarpUi.toast(S('logs.clearFailed', e.message), 'error');
            }
        });

        var chartsVisible = true;
        try { chartsVisible = window.localStorage.getItem('yarpui.logs.chartsVisible') !== '0'; } catch (e) { /* private mode */ }
        applyChartsVisible(chartsVisible);

        initChart();
        loadRetention();
        loadFilterOptions();
        queryLogs();
        fetchStats();
        window.setInterval(function () { if (auto) { poll(); } }, 2000);
        window.setInterval(function () { if (auto) { fetchStats(); } }, 5000);
    });
})();
