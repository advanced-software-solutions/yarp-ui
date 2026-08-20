/* Request logs: polls /api/yarp/logs every 2s while Live is on, with client-side filtering.
   The performance panel aggregates the same data: /api/yarp/logs/stats every 5s (stat cards,
   per-route bars, P95 line) and a per-request duration scatter fed from the polled entries. */
(function () {
    'use strict';

    var esc = window.YarpUi.esc;

    var entries = [];
    var lastSeq = 0;
    var inFlight = false;

    var filterText = '';
    var filterStatus = '';
    var auto = true;

    var MAX_CLIENT_ENTRIES = 500;

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

    function fmtMs(value) {
        if (value == null || isNaN(value)) { return '—'; }
        if (value >= 1000) { return (value / 1000).toFixed(2) + ' s'; }
        if (value >= 100) { return Math.round(value) + ' ms'; }
        return value.toFixed(1) + ' ms';
    }

    function passesFilter(entry) {
        if (filterStatus && Math.floor((entry.statusCode || 0) / 100) !== +filterStatus) {
            return false;
        }
        if (filterText) {
            var q = filterText.toLowerCase();
            var haystack = (entry.path || '') + ' ' + (entry.routeId || '') + ' ' + (entry.clusterId || '') + ' ' + (entry.destinationId || '') + ' ' + (entry.method || '');
            if (haystack.toLowerCase().indexOf(q) === -1) { return false; }
        }
        return true;
    }

    /* ---- table ---- */

    function render() {
        var rows = document.getElementById('log-rows');
        var visible = entries.filter(passesFilter);
        document.getElementById('log-count').textContent =
            visible.length + ' shown · ' + entries.length + ' buffered';

        if (!entries.length) {
            rows.innerHTML = '';
            document.getElementById('log-empty').classList.remove('hidden');
            renderChart();
            return;
        }
        document.getElementById('log-empty').classList.add('hidden');

        rows.innerHTML = visible.map(function (e) {
            var status = e.statusCode == null ? '—' : e.statusCode;
            var title = e.error ? ' title="' + esc(e.error) + '"' : '';
            return '<tr class="' + statusClass(e.statusCode) + '"' + title + '>' +
                '<td class="col-time mono">' + formatTime(e.timestampUtc) + '</td>' +
                '<td class="col-method"><span class="method-pill m-' + esc((e.method || '').toLowerCase()) + '">' + esc(e.method) + '</span></td>' +
                '<td class="mono cell-path" title="' + esc(e.path) + '">' + esc(e.path) + '</td>' +
                '<td class="col-status"><span class="status-pill ' + statusClass(e.statusCode) + '">' + status + '</span></td>' +
                '<td class="col-duration mono">' + (e.durationMs == null ? '—' : e.durationMs.toFixed(1) + ' ms') + '</td>' +
                '<td class="mono cell-dim">' + esc(e.routeId || '—') + '</td>' +
                '<td class="mono cell-dim">' + esc(e.clusterId || '—') + '</td>' +
                '<td class="mono cell-dim" title="' + esc(e.destinationAddress || '') + '">' + esc(e.destinationId || '—') + '</td>' +
                '</tr>';
        }).join('');

        renderChart();
    }

    async function poll() {
        if (inFlight) { return; }
        inFlight = true;
        try {
            var res = await window.YarpUi.api('/api/yarp/logs?after=' + lastSeq);
            if (res.ok) {
                var data = await res.json();
                if (data.entries && data.entries.length) {
                    entries = entries.concat(data.entries);
                    if (entries.length > MAX_CLIENT_ENTRIES) {
                        entries = entries.slice(entries.length - MAX_CLIENT_ENTRIES);
                    }
                    lastSeq = data.entries[data.entries.length - 1].seq;
                    render();
                }
            }
        } catch (e) {
            /* transient — retried on the next tick */
        } finally {
            inFlight = false;
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
                label: group,
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
                        ticks: { callback: function (value) { return value + ' ms'; } },
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
                                    return 'P95 ' + fmtMs(item.parsed.y);
                                }
                                var entry = item.raw.entry;
                                if (!entry) { return fmtMs(item.parsed.y); }
                                return ' ' + formatTime(entry.timestampUtc) + ' · ' + (entry.statusCode == null ? 'failed' : entry.statusCode) +
                                    ' · ' + fmtMs(entry.durationMs) + ' · ' + (entry.routeId || '(no route)');
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

        document.getElementById('stats-caption').textContent =
            (statsWindow > 0 ? 'last ' + (statsWindow >= 60 ? (statsWindow / 60) + ' h' : statsWindow + ' min') : 'all time') +
            ' · updated ' + pad(new Date().getHours()) + ':' + pad(new Date().getMinutes()) + ':' + pad(new Date().getSeconds());

        renderRouteBars(stats.routes || []);
    }

    function renderRouteBars(routes) {
        var list = document.getElementById('route-bars-list');
        if (!routes.length) {
            list.innerHTML = '<div class="muted small route-bars-empty">No requests in this window yet.</div>';
            return;
        }

        var top = routes.slice(0, 8);
        var maxP95 = Math.max.apply(null, top.map(function (r) { return r.p95Ms; })) || 1;

        list.innerHTML = top.map(function (r) {
            var width = Math.max(2, Math.round((r.p95Ms / maxP95) * 100));
            var meta = r.count.toLocaleString() + ' reqs · avg ' + fmtMs(r.avgMs) + ' · max ' + fmtMs(r.maxMs);
            if (r.errorCount > 0) { meta += ' · <span class="route-bar-errors">' + r.errorCount.toLocaleString() + ' errors</span>'; }
            return '<div class="route-bar-row">' +
                '<div class="route-bar-top"><span class="mono" title="' + esc(r.routeId || '(no route)') + '">' + esc(r.routeId || '(no route)') + '</span>' +
                '<span class="muted small">' + meta + '</span></div>' +
                '<div class="route-bar-track"><div class="route-bar-fill" style="width:' + width + '%"></div></div>' +
                '</div>';
        }).join('');
    }

    /* ---- retention policy ---- */

    function setRetentionValue(days) {
        var select = document.getElementById('log-retention');
        var exists = Array.prototype.some.call(select.options, function (o) { return +o.value === days; });
        if (!exists) {
            var option = document.createElement('option');
            option.value = String(days);
            option.textContent = 'Keep logs: ' + days + ' days';
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
                window.YarpUi.toast(days === 0
                    ? 'Logs are now kept forever.'
                    : 'Logs older than ' + days + ' ' + (days === 1 ? 'day' : 'days') + ' will be deleted automatically.', 'success');
            } else {
                var data = await res.json().catch(function () { return null; });
                window.YarpUi.toast('Could not save the retention policy: ' + ((data && data.errors && data.errors[0]) || res.status), 'error');
                loadRetention(); // snap the select back to the stored value
            }
        } catch (e) {
            window.YarpUi.toast('Could not save the retention policy: ' + e.message, 'error');
        }
    }

    /* ---- panel collapse ---- */

    function applyChartsVisible(visible) {
        document.getElementById('charts-body').classList.toggle('hidden', !visible);
        document.getElementById('charts-toggle').classList.toggle('collapsed', !visible);
        document.getElementById('charts-toggle').setAttribute('aria-expanded', String(visible));
        try { window.localStorage.setItem('yarpui.logs.chartsVisible', visible ? '1' : '0'); } catch (e) { /* private mode */ }
    }

    /* ---- wiring ---- */

    document.addEventListener('DOMContentLoaded', function () {
        document.getElementById('log-search').addEventListener('input', window.YarpUi.debounce(function (e) {
            filterText = e.target.value.trim();
            render();
        }, 140));

        document.getElementById('log-status').addEventListener('change', function (e) {
            filterStatus = e.target.value;
            render();
        });

        var autoToggle = document.getElementById('log-auto');
        autoToggle.addEventListener('change', function (e) {
            auto = e.target.checked;
            document.getElementById('live-dot').classList.toggle('on', auto);
        });
        document.getElementById('live-dot').classList.add('on');

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
                render();
                fetchStats();
                window.YarpUi.toast('Stored request logs deleted.', 'success');
            } catch (e) {
                window.YarpUi.toast('Clear failed: ' + e.message, 'error');
            }
        });

        var chartsVisible = true;
        try { chartsVisible = window.localStorage.getItem('yarpui.logs.chartsVisible') !== '0'; } catch (e) { /* private mode */ }
        applyChartsVisible(chartsVisible);

        initChart();
        loadRetention();
        render();
        poll();
        fetchStats();
        window.setInterval(function () { if (auto) { poll(); } }, 2000);
        window.setInterval(function () { if (auto) { fetchStats(); } }, 5000);
    });
})();
