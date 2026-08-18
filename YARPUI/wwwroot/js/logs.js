/* Request logs: polls /api/yarp/logs every 2s while Live is on, with client-side filtering. */
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

    function statusClass(code) {
        if (code == null) { return 's-none'; }
        if (code >= 500) { return 's-5'; }
        if (code >= 400) { return 's-4'; }
        if (code >= 300) { return 's-3'; }
        if (code >= 200) { return 's-2'; }
        return 's-none';
    }

    function formatTime(iso) {
        var d = new Date(iso);
        var pad = function (n) { return (n < 10 ? '0' : '') + n; };
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(Math.floor(d.getMilliseconds() / 10));
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

    function render() {
        var rows = document.getElementById('log-rows');
        var visible = entries.filter(passesFilter);
        document.getElementById('log-count').textContent =
            visible.length + ' shown · ' + entries.length + ' buffered';

        if (!entries.length) {
            rows.innerHTML = '';
            document.getElementById('log-empty').classList.remove('hidden');
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

        document.getElementById('log-clear').addEventListener('click', async function () {
            try {
                await window.YarpUi.api('/api/yarp/logs', { method: 'DELETE' });
                entries = [];
                lastSeq = 0;
                render();
                window.YarpUi.toast('Log buffer cleared.', 'success');
            } catch (e) {
                window.YarpUi.toast('Clear failed: ' + e.message, 'error');
            }
        });

        render();
        poll();
        window.setInterval(function () {
            if (auto) { poll(); }
        }, 2000);
    });
})();
