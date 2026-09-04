/* IP blocking: rules are managed through /api/yarp/ipblocking — single addresses, CIDR
   networks and from–to ranges. Every change applies immediately (the server swaps a
   precompiled matcher snapshot; no restart, no proxy downtime). The settings toggle decides
   which address is matched: the direct connection by default, or the X-Forwarded-For header
   for deployments chained behind a trusted proxy. */
(function () {
    'use strict';

    var esc = window.YarpUi.esc;
    var S = window.YarpUi.S;
    var Sn = window.YarpUi.Sn;

    var rules = [];

    var KIND_KEYS = {
        single: 'ipblocking.kindSingle',
        cidr: 'ipblocking.kindCidr',
        range: 'ipblocking.kindRange'
    };

    function formatDate(iso) {
        var d = new Date(iso);
        if (isNaN(d.getTime())) { return '—'; }
        return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    function render() {
        document.getElementById('block-count').textContent = Sn('ipblocking.count', rules.length);
        document.getElementById('block-empty').classList.toggle('hidden', rules.length !== 0);

        document.getElementById('block-rows').innerHTML = rules.map(function (r) {
            var kind = S(KIND_KEYS[r.kind] || 'ipblocking.kindSingle');
            return '<tr>' +
                '<td class="mono">' + esc(r.value) + '</td>' +
                '<td><span class="pill">' + esc(kind) + '</span></td>' +
                '<td class="cell-dim" title="' + esc(r.note || '') + '">' + esc(r.note || '—') + '</td>' +
                '<td class="mono cell-dim">' + formatDate(r.createdAtUtc) + '</td>' +
                '<td class="col-actions">' +
                '<button type="button" class="btn btn-danger-ghost btn-sm block-remove" data-id="' + esc(r.id) + '" data-value="' + esc(r.value) + '" title="' + esc(S('ipblocking.deleteTitle')) + '">' +
                window.YarpUi.icon('trash') +
                '</button></td>' +
                '</tr>';
        }).join('');
    }

    async function load() {
        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking');
            if (!res.ok) { return; }
            var data = await res.json();
            rules = data.rules || [];
            document.getElementById('block-xff').checked = !!(data.settings && data.settings.trustForwardedFor);
            render();
        } catch (e) {
            /* transient — the table keeps its previous content */
        }
    }

    async function addRule() {
        var value = document.getElementById('block-value').value.trim();
        var note = document.getElementById('block-note').value.trim();
        if (!value) {
            window.YarpUi.toast(S('ipblocking.addFailed', S('validation.ipRequired')), 'error');
            return;
        }

        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking/rules', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ value: value, note: note })
            });
            if (res.ok) {
                var data = await res.json();
                document.getElementById('block-value').value = '';
                document.getElementById('block-note').value = '';
                window.YarpUi.toast(S('ipblocking.added', data.rule.value), 'success');
                load();
            } else {
                var failure = await res.json().catch(function () { return null; });
                window.YarpUi.toast(S('ipblocking.addFailed', (failure && failure.errors && failure.errors[0]) || res.status), 'error');
            }
        } catch (e) {
            window.YarpUi.toast(S('ipblocking.addFailed', e.message), 'error');
        }
    }

    async function removeRule(button) {
        var value = button.getAttribute('data-value');
        if (!window.confirm(S('ipblocking.confirmDelete', value))) { return; }

        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking/rules/' + encodeURIComponent(button.getAttribute('data-id')), { method: 'DELETE' });
            if (res.ok) {
                window.YarpUi.toast(S('ipblocking.removed'), 'success');
                load();
            } else {
                window.YarpUi.toast(S('ipblocking.removeFailed', res.status), 'error');
            }
        } catch (e) {
            window.YarpUi.toast(S('ipblocking.removeFailed', e.message), 'error');
        }
    }

    async function saveSettings(trustForwardedFor) {
        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ trustForwardedFor: trustForwardedFor })
            });
            if (res.ok) {
                window.YarpUi.toast(S('ipblocking.settingsSaved'), 'success');
            } else {
                window.YarpUi.toast(S('ipblocking.settingsSaveFailed', res.status), 'error');
                load(); // snap the toggle back to the stored value
            }
        } catch (e) {
            window.YarpUi.toast(S('ipblocking.settingsSaveFailed', e.message), 'error');
        }
    }

    async function checkAddress() {
        var input = document.getElementById('block-check-ip');
        var output = document.getElementById('block-check-result');
        var ip = input.value.trim();
        if (!ip) { return; }

        try {
            var res = await window.YarpUi.api('/api/yarp/ipblocking/check', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ip: ip })
            });
            var data = await res.json().catch(function () { return null; });
            if (res.ok && data) {
                output.textContent = data.blocked
                    ? S('ipblocking.checkBlocked', ip, data.value || '')
                    : S('ipblocking.checkAllowed', ip);
                output.classList.toggle('check-blocked', !!data.blocked);
            } else {
                output.textContent = (data && data.errors && data.errors[0]) || S('ipblocking.checkFailed');
                output.classList.remove('check-blocked');
            }
        } catch (e) {
            output.textContent = S('ipblocking.checkFailed');
            output.classList.remove('check-blocked');
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.getElementById('block-add').addEventListener('click', addRule);
        document.getElementById('block-value').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { addRule(); }
        });
        document.getElementById('block-rows').addEventListener('click', function (e) {
            var button = e.target.closest ? e.target.closest('.block-remove') : null;
            if (button) { removeRule(button); }
        });
        document.getElementById('block-xff').addEventListener('change', function (e) {
            saveSettings(e.target.checked);
        });
        document.getElementById('block-check').addEventListener('click', checkAddress);
        document.getElementById('block-check-ip').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { checkAddress(); }
        });

        load();
    });
})();
