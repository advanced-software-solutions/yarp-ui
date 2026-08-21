/* Configuration editor: full CRUD for routes and clusters, saved atomically to /api/yarp/config. */
(function () {
    'use strict';

    var esc = window.YarpUi.esc;
    var icon = window.YarpUi.icon;
    var S = window.YarpUi.S;
    var Sn = window.YarpUi.Sn;

    var LB_POLICIES = ['', 'First', 'Random', 'RoundRobin', 'LeastRequests', 'PowerOfTwoChoices'];

    var routes = [];
    var clusters = [];
    var baseline = '';
    var selected = null; // { type: 'route'|'cluster', id: string }
    var loading = false;
    var editableRoutes = new Set(); // ids the editor can change (attach mode: everything defined in appsettings files)
    var editableClusters = new Set();
    var attachMode = false;

    // ---------- model helpers ----------

    function routeById(id) { return routes.find(function (r) { return r.RouteId === id; }); }
    function clusterById(id) { return clusters.find(function (c) { return c.ClusterId === id; }); }

    function uniqueName(base) {
        var name = base;
        var n = 1;
        while (routeById(name) || clusterById(name)) {
            n += 1;
            name = base + '-' + n;
        }
        return name;
    }

    function normalizeRoute(r) {
        return {
            RouteId: r.RouteId,
            ClusterId: r.ClusterId || '',
            Order: r.Order == null ? null : r.Order,
            Match: {
                Path: (r.Match && r.Match.Path) || '',
                Hosts: (r.Match && r.Match.Hosts) ? r.Match.Hosts.slice() : [],
                Methods: (r.Match && r.Match.Methods) ? r.Match.Methods.slice() : []
            },
            AuthorizationPolicy: r.AuthorizationPolicy || '',
            CorsPolicy: r.CorsPolicy || '',
            _transformsText: JSON.stringify(r.Transforms || [], null, 2),
            _transformsValid: true
        };
    }

    function normalizeCluster(c) {
        var rows = [];
        Object.keys(c.Destinations || {}).forEach(function (name) {
            rows.push({ name: name, address: (c.Destinations[name] && c.Destinations[name].Address) || '' });
        });
        return {
            ClusterId: c.ClusterId,
            LoadBalancingPolicy: c.LoadBalancingPolicy || '',
            _destRows: rows,
            _healthRaw: c.HealthCheck ? JSON.parse(JSON.stringify(c.HealthCheck)) : null,
            _healthActive: !!(c.HealthCheck && c.HealthCheck.Active && c.HealthCheck.Active.Enabled),
            _healthPassive: !!(c.HealthCheck && c.HealthCheck.Passive && c.HealthCheck.Passive.Enabled)
        };
    }

    function applyConfig(cfg) {
        routes = (cfg.Routes || []).map(normalizeRoute);
        clusters = (cfg.Clusters || []).map(normalizeCluster);
        editableRoutes = new Set(cfg.EditableRouteIds || routes.map(function (r) { return r.RouteId; }));
        editableClusters = new Set(cfg.EditableClusterIds || clusters.map(function (c) { return c.ClusterId; }));
        attachMode = !!cfg.AttachMode;
        if (selected) {
            if (selected.type === 'route' && !routeById(selected.id)) { selected = null; }
            if (selected.type === 'cluster' && !clusterById(selected.id)) { selected = null; }
        }
        updateSourcePill(cfg);
        updateResetButton();
        baseline = JSON.stringify(buildDoc());
    }

    function isEditableRoute(id) { return editableRoutes.has(id); }
    function isEditableCluster(id) { return editableClusters.has(id); }

    function updateSourcePill(cfg) {
        var pill = document.getElementById('source-pill');
        if (!pill) { return; }
        if (cfg && cfg.AttachMode) {
            pill.textContent = S('pill.attachMode');
            pill.className = 'pill pill-accent';
            pill.title = S('pill.attachModeTitle');
        } else if (cfg && cfg.ManagedByUi) {
            pill.textContent = S('pill.uiManaged');
            pill.className = 'pill pill-accent';
            pill.title = S('pill.uiManagedTitle');
        } else {
            pill.textContent = 'appsettings.json';
            pill.className = 'pill';
            pill.title = S('pill.appsettingsTitle');
        }
    }

    function updateResetButton() {
        var btn = document.getElementById('btn-reset');
        if (btn) {
            btn.textContent = attachMode ? S('editor.restoreBackup') : S('editor.resetToAppsettings');
            btn.title = attachMode ? S('editor.restoreTitle') : S('editor.resetTitle');
        }
    }

    function parseTransforms(route) {
        try {
            var parsed = JSON.parse(route._transformsText);
            if (!Array.isArray(parsed)) { return null; }
            return parsed;
        } catch (e) {
            return null;
        }
    }

    function buildHealthCheck(model) {
        var raw = model._healthRaw;
        if (!raw && !model._healthActive && !model._healthPassive) { return null; }
        var hc = raw ? JSON.parse(JSON.stringify(raw)) : {};
        if (model._healthActive) {
            hc.Active = Object.assign({}, hc.Active || {}, { Enabled: true });
        } else if (hc.Active) {
            hc.Active.Enabled = false;
        }
        if (model._healthPassive) {
            hc.Passive = Object.assign({}, hc.Passive || {}, { Enabled: true });
        } else if (hc.Passive) {
            hc.Passive.Enabled = false;
        }
        return hc;
    }

    function splitList(text) {
        return text.split(',').map(function (s) { return s.trim(); }).filter(Boolean);
    }

    function buildDoc() {
        // In attach mode the payload carries every editable item; edits are written back to appsettings.
        var docRoutes = routes.filter(function (r) { return isEditableRoute(r.RouteId); }).map(function (r) {
            var transforms = parseTransforms(r);
            return {
                RouteId: r.RouteId,
                ClusterId: r.ClusterId || null,
                Order: r.Order,
                Match: {
                    Path: r.Match.Path || null,
                    Hosts: r.Match.Hosts.length ? r.Match.Hosts : null,
                    Methods: r.Match.Methods.length ? r.Match.Methods : null
                },
                AuthorizationPolicy: r.AuthorizationPolicy || null,
                CorsPolicy: r.CorsPolicy || null,
                Transforms: transforms && transforms.length ? transforms : null
            };
        });

        var docClusters = clusters.filter(function (c) { return isEditableCluster(c.ClusterId); }).map(function (c) {
            var destinations = {};
            c._destRows.forEach(function (row) {
                if (row.name.trim()) {
                    destinations[row.name.trim()] = { Address: row.address.trim() };
                }
            });
            return {
                ClusterId: c.ClusterId,
                LoadBalancingPolicy: c.LoadBalancingPolicy || null,
                Destinations: destinations,
                HealthCheck: buildHealthCheck(c)
            };
        });

        return { Routes: docRoutes, Clusters: docClusters };
    }

    function transformsInvalidCount() {
        return routes.filter(function (r) { return parseTransforms(r) === null; }).length;
    }

    function isDirty() {
        return JSON.stringify(buildDoc()) !== baseline;
    }

    // ---------- rendering ----------

    function renderList() {
        var routeList = document.getElementById('route-list');
        var clusterList = document.getElementById('cluster-list');
        document.getElementById('count-routes').textContent = routes.length;
        document.getElementById('count-clusters').textContent = clusters.length;

        routeList.innerHTML = routes.map(function (r) {
            var active = selected && selected.type === 'route' && selected.id === r.RouteId;
            var broken = !r.ClusterId || !clusterById(r.ClusterId);
            var managed = isEditableRoute(r.RouteId);
            var sub = r.Match.Path || (r.Match.Hosts.length ? r.Match.Hosts.join(', ') : S('map.anyHostPath'));
            var title = managed ? esc(r.RouteId) : esc(S('editor.itemExternalTitle', r.RouteId));
            return '<li class="item' + (active ? ' active' : '') + (managed ? '' : ' item-external') + '" data-type="route" data-id="' + esc(r.RouteId) + '" title="' + title + '">' +
                '<span class="item-icon item-icon-route">' + icon('route') + '</span>' +
                '<span class="item-text"><span class="item-title">' + esc(r.RouteId) + '</span><span class="item-sub">' + esc(sub) + '</span></span>' +
                '<span class="item-chip' + (broken ? ' item-chip-warn' : '') + '" title="' + esc(S('map.kvCluster')) + '">' + esc(r.ClusterId || S('editor.noCluster')) + '</span>' +
                (managed
                    ? '<button type="button" class="item-del" title="' + esc(S('editor.deleteRouteTitle')) + '">' + icon('trash') + '</button>'
                    : '<span class="item-lock" title="' + esc(S('editor.lockTitle')) + '">' + icon('lock') + '</span>') +
                '</li>';
        }).join('') || '<li class="item-empty">' + esc(S('editor.noRoutes')) + '</li>';

        clusterList.innerHTML = clusters.map(function (c) {
            var active = selected && selected.type === 'cluster' && selected.id === c.ClusterId;
            var managed = isEditableCluster(c.ClusterId);
            var title = managed ? esc(c.ClusterId) : esc(S('editor.itemExternalTitle', c.ClusterId));
            var sub = Sn('editor.destCount', c._destRows.length) + ' · ' + esc(c.LoadBalancingPolicy || 'PowerOfTwoChoices');
            return '<li class="item' + (active ? ' active' : '') + (managed ? '' : ' item-external') + '" data-type="cluster" data-id="' + esc(c.ClusterId) + '" title="' + title + '">' +
                '<span class="item-icon item-icon-cluster">' + icon('cluster') + '</span>' +
                '<span class="item-text"><span class="item-title">' + esc(c.ClusterId) + '</span><span class="item-sub">' + sub + '</span></span>' +
                (managed
                    ? '<button type="button" class="item-del" title="' + esc(S('editor.deleteClusterTitle')) + '">' + icon('trash') + '</button>'
                    : '<span class="item-lock" title="' + esc(S('editor.lockTitle')) + '">' + icon('lock') + '</span>') +
                '</li>';
        }).join('') || '<li class="item-empty">' + esc(S('editor.noClusters')) + '</li>';
    }

    function field(label, inner, full, hint) {
        return '<div class="field' + (full ? ' field-full' : '') + '">' +
            '<label>' + esc(label) + '</label>' + inner +
            (hint ? '<div class="field-hint">' + hint + '</div>' : '') +
            '</div>';
    }

    function renderRouteForm(r) {
        var clusterOptions = clusters.map(function (c) {
            var sel = c.ClusterId === r.ClusterId ? ' selected' : '';
            return '<option value="' + esc(c.ClusterId) + '"' + sel + '>' + esc(c.ClusterId) + '</option>';
        }).join('');

        var transformsHint = parseTransforms(r) === null
            ? '<span class="hint-error">' + esc(S('editor.transformsInvalid')) + '</span>'
            : '<span class="muted">' + esc(S('editor.transformsHint')) + '</span>';

        return '' +
            '<div class="form-card">' +
                '<div class="form-head">' +
                    '<span class="item-icon item-icon-route">' + icon('route') + '</span>' +
                    '<span class="form-title">' + esc(r.RouteId) + '</span>' +
                    '<span class="type-chip type-route">' + esc(S('map.typeRoute')) + '</span>' +
                '</div>' +
                '<div class="form-grid">' +
                    field(S('editor.fieldRouteId'), '<input type="text" data-field="RouteId" value="' + esc(r.RouteId) + '" spellcheck="false">') +
                    field(S('editor.fieldCluster'), '<select data-field="ClusterId"><option value="">' + esc(S('editor.selectCluster')) + '</option>' + clusterOptions + '</select>') +
                    field(S('editor.fieldMatchPath'), '<input type="text" data-field="Match.Path" value="' + esc(r.Match.Path) + '" placeholder="/api/{**catch-all}" spellcheck="false">') +
                    field(S('editor.fieldOrder'), '<input type="number" data-field="Order" value="' + (r.Order == null ? '' : r.Order) + '" placeholder="' + esc(S('editor.phOrder')) + '">') +
                    field(S('editor.fieldHosts'), '<input type="text" data-field="Match.Hosts" value="' + esc(r.Match.Hosts.join(', ')) + '" placeholder="api.example.com, docs.example.com" spellcheck="false">', false, '') +
                    field(S('editor.fieldMethods'), '<input type="text" data-field="Match.Methods" value="' + esc(r.Match.Methods.join(', ')) + '" placeholder="' + esc(S('editor.phMethods')) + '" spellcheck="false">') +
                    field(S('editor.fieldAuthorization'), '<input type="text" data-field="AuthorizationPolicy" value="' + esc(r.AuthorizationPolicy) + '" placeholder="' + esc(S('editor.phPolicy')) + '" spellcheck="false">') +
                    field(S('editor.fieldCors'), '<input type="text" data-field="CorsPolicy" value="' + esc(r.CorsPolicy) + '" placeholder="' + esc(S('editor.phPolicy')) + '" spellcheck="false">') +
                    field(S('editor.fieldTransforms'), '<textarea data-field="Transforms" rows="9" class="mono" spellcheck="false">' + esc(r._transformsText) + '</textarea>', true, transformsHint) +
                '</div>' +
            '</div>';
    }

    function renderClusterForm(c) {
        var policyOptions = LB_POLICIES.map(function (p) {
            var sel = p === c.LoadBalancingPolicy ? ' selected' : '';
            var label = p === '' ? S('map.lbDefault') : p;
            return '<option value="' + esc(p) + '"' + sel + '>' + esc(label) + '</option>';
        }).join('');

        var rows = c._destRows.map(function (row, index) {
            return '<div class="dest-row" data-index="' + index + '">' +
                '<input type="text" class="dest-name" data-dest="name" data-index="' + index + '" value="' + esc(row.name) + '" placeholder="' + esc(S('editor.phDestName')) + '" spellcheck="false">' +
                '<input type="text" class="dest-address mono" data-dest="address" data-index="' + index + '" value="' + esc(row.address) + '" placeholder="https://service.internal/" spellcheck="false">' +
                '<button type="button" class="btn btn-ghost btn-icon dest-del" data-index="' + index + '" title="' + esc(S('editor.removeDestination')) + '">' + icon('trash') + '</button>' +
                '</div>';
        }).join('');

        return '' +
            '<div class="form-card">' +
                '<div class="form-head">' +
                    '<span class="item-icon item-icon-cluster">' + icon('cluster') + '</span>' +
                    '<span class="form-title">' + esc(c.ClusterId) + '</span>' +
                    '<span class="type-chip type-cluster">' + esc(S('map.typeCluster')) + '</span>' +
                '</div>' +
                '<div class="form-grid">' +
                    field(S('editor.fieldClusterId'), '<input type="text" data-field="ClusterId" value="' + esc(c.ClusterId) + '" spellcheck="false">') +
                    field(S('editor.fieldLbPolicy'), '<select data-field="LoadBalancingPolicy">' + policyOptions + '</select>') +
                    '<div class="field field-full">' +
                        '<label>' + esc(S('editor.fieldDestinations')) + '</label>' +
                        '<div class="dest-list">' + (rows || '<div class="item-empty">' + esc(S('editor.noDestinations')) + '</div>') + '</div>' +
                        '<button type="button" class="btn btn-ghost btn-sm dest-add">' + icon('plus') + ' ' + esc(S('editor.addDestination')) + '</button>' +
                    '</div>' +
                    '<div class="field"><label class="checkline"><input type="checkbox" data-field="HealthActive"' + (c._healthActive ? ' checked' : '') + '> ' + esc(S('editor.fieldActiveHealth')) + '</label></div>' +
                    '<div class="field"><label class="checkline"><input type="checkbox" data-field="HealthPassive"' + (c._healthPassive ? ' checked' : '') + '> ' + esc(S('editor.fieldPassiveHealth')) + '</label></div>' +
                '</div>' +
            '</div>';
    }

    function renderForm() {
        var main = document.getElementById('editor-main');
        if (!selected) {
            main.innerHTML =
                '<div class="editor-placeholder">' +
                    '<div class="empty-icon">' + icon('edit') + '</div>' +
                    '<h2>' + esc(S('editor.nothingSelected')) + '</h2>' +
                    '<p class="muted">' + esc(S('editor.pickItem')) + '</p>' +
                '</div>';
            return;
        }
        var managed = selected.type === 'route' ? isEditableRoute(selected.id) : isEditableCluster(selected.id);
        var banner = managed ? '' :
            '<div class="ro-banner">' + icon('lock') +
            ' ' + esc(S('editor.roBanner', selected.type === 'route' ? S('map.typeRoute') : S('map.typeCluster'))) + '</div>';
        var html;
        if (selected.type === 'route') {
            var r = routeById(selected.id);
            html = r ? renderRouteForm(r) : '';
        } else {
            var c = clusterById(selected.id);
            html = c ? renderClusterForm(c) : '';
        }
        if (html && !managed) {
            html = html.replace('<div class="form-grid">', banner + '<div class="form-grid">');
            html = html.replace(/<(input|select|textarea)(\s)/g, '<$1 disabled$2');
        }
        main.innerHTML = html;
    }

    function updateDirtyState() {
        var dirty = isDirty();
        var invalid = transformsInvalidCount();
        document.getElementById('dirty-indicator').classList.toggle('hidden', !dirty);
        var save = document.getElementById('btn-save');
        save.disabled = loading || !dirty || invalid > 0;
        save.title = invalid > 0 ? S('editor.fixTransforms') : S('editor.saveTitle');
    }

    function refreshSelection() {
        renderList();
        renderForm();
        updateDirtyState();
    }

    // ---------- selection & mutations ----------

    function select(type, id) {
        selected = { type: type, id: id };
        renderList();
        renderForm();
        if (selected) {
            var el = document.querySelector('.editor-main .form-card');
            if (el) { el.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
        }
    }

    function handleRename(type, oldId, newId, input) {
        newId = (newId || '').trim();
        var siblings = type === 'route' ? routes : clusters;
        var taken = siblings.some(function (m) {
            return m !== (type === 'route' ? routeById(oldId) : clusterById(oldId)) &&
                (type === 'route' ? m.RouteId : m.ClusterId).toLowerCase() === newId.toLowerCase();
        });
        if (!newId || taken) {
            window.YarpUi.toast(taken ? S('editor.idInUse') : S('editor.idEmpty'), 'error');
            input.value = oldId;
            return;
        }
        if (type === 'route') {
            routeById(oldId).RouteId = newId;
            editableRoutes.delete(oldId);
            editableRoutes.add(newId);
        } else {
            clusterById(oldId).ClusterId = newId;
            editableClusters.delete(oldId);
            editableClusters.add(newId);
            routes.forEach(function (r) {
                if (r.ClusterId === oldId) { r.ClusterId = newId; }
            });
        }
        selected = { type: type, id: newId };
        renderList();
        updateDirtyState();
    }

    function deleteRoute(id) {
        if (!isEditableRoute(id)) { return; }
        if (!window.confirm(S('editor.confirmDeleteRoute', id))) { return; }
        routes = routes.filter(function (r) { return r.RouteId !== id; });
        editableRoutes.delete(id);
        if (selected && selected.type === 'route' && selected.id === id) { selected = null; }
        refreshSelection();
    }

    function deleteCluster(id) {
        if (!isEditableCluster(id)) { return; }
        var refs = routes.filter(function (r) { return r.ClusterId === id; }).length;
        var msg = S('editor.confirmDeleteCluster', id);
        if (refs) { msg += '\n' + Sn('editor.routesRef', refs); }
        if (!window.confirm(msg)) { return; }
        clusters = clusters.filter(function (c) { return c.ClusterId !== id; });
        editableClusters.delete(id);
        if (selected && selected.type === 'cluster' && selected.id === id) { selected = null; }
        refreshSelection();
    }

    // ---------- field binding ----------

    function bindForm() {
        var main = document.getElementById('editor-main');

        main.addEventListener('input', function (e) {
            var t = e.target;
            if (!selected || loading) { return; }

            // Foreign items are read-only; their inputs are disabled but stay defensive here.
            if (selected.type === 'route' && !isEditableRoute(selected.id)) { return; }
            if (selected.type === 'cluster' && !isEditableCluster(selected.id)) { return; }

            if (t.dataset.dest !== undefined && selected.type === 'cluster') {
                var row = clusterById(selected.id)._destRows[+t.dataset.index];
                if (row) {
                    row[t.dataset.dest] = t.value;
                    updateDirtyState();
                }
                return;
            }

            var fieldPath = t.dataset.field;
            if (!fieldPath) { return; }

            if (selected.type === 'route') {
                var r = routeById(selected.id);
                if (!r) { return; }
                switch (fieldPath) {
                    case 'RouteId': return; // handled on change (blur)
                    case 'ClusterId': r.ClusterId = t.value; break;
                    case 'Order': r.Order = t.value === '' ? null : +t.value; break;
                    case 'Match.Path': r.Match.Path = t.value; break;
                    case 'Match.Hosts': r.Match.Hosts = splitList(t.value); break;
                    case 'Match.Methods': r.Match.Methods = splitList(t.value); break;
                    case 'AuthorizationPolicy': r.AuthorizationPolicy = t.value; break;
                    case 'CorsPolicy': r.CorsPolicy = t.value; break;
                    case 'Transforms':
                        r._transformsText = t.value;
                        r._transformsValid = parseTransforms(r) !== null;
                        var hint = t.closest('.field').querySelector('.field-hint');
                        if (hint) {
                            hint.innerHTML = r._transformsValid
                                ? '<span class="muted">' + esc(S('editor.transformsHint')) + '</span>'
                                : '<span class="hint-error">' + esc(S('editor.transformsInvalid')) + '</span>';
                        }
                        break;
                }
                updateListLine('route', r.RouteId);
            } else {
                var c = clusterById(selected.id);
                if (!c) { return; }
                switch (fieldPath) {
                    case 'ClusterId': return; // handled on change (blur)
                    case 'LoadBalancingPolicy': c.LoadBalancingPolicy = t.value; break;
                }
                updateListLine('cluster', c.ClusterId);
            }
            updateDirtyState();
        });

        main.addEventListener('change', function (e) {
            var t = e.target;
            if (!selected || loading) { return; }

            if (selected.type === 'route' && !isEditableRoute(selected.id)) { return; }
            if (selected.type === 'cluster' && !isEditableCluster(selected.id)) { return; }

            if (t.dataset.field === 'RouteId' && selected.type === 'route') {
                var r = routeById(selected.id);
                if (r && t.value !== r.RouteId) {
                    handleRename('route', r.RouteId, t.value, t);
                }
                return;
            }
            if (t.dataset.field === 'ClusterId' && selected.type === 'cluster') {
                var c = clusterById(selected.id);
                if (c && t.value !== c.ClusterId) {
                    handleRename('cluster', c.ClusterId, t.value, t);
                }
                return;
            }
            if ((t.dataset.field === 'HealthActive' || t.dataset.field === 'HealthPassive') && selected.type === 'cluster') {
                var cl = clusterById(selected.id);
                if (!cl) { return; }
                if (t.dataset.field === 'HealthActive') { cl._healthActive = t.checked; }
                else { cl._healthPassive = t.checked; }
                updateDirtyState();
            }
        });

        main.addEventListener('click', function (e) {
            var del = e.target.closest('.dest-del');
            if (del && selected && selected.type === 'cluster') {
                clusterById(selected.id)._destRows.splice(+del.dataset.index, 1);
                renderForm();
                updateDirtyState();
                return;
            }
            if (e.target.closest('.dest-add') && selected && selected.type === 'cluster') {
                clusterById(selected.id)._destRows.push({ name: '', address: '' });
                renderForm();
                var inputs = main.querySelectorAll('.dest-row:last-child .dest-name');
                if (inputs.length) { inputs[0].focus(); }
                updateDirtyState();
            }
        });
    }

    function updateListLine(type, id) {
        var li = document.querySelector('.item[data-type="' + type + '"][data-id="' + (window.CSS && CSS.escape ? CSS.escape(id) : id) + '"]');
        if (!li) { return; }
        if (type === 'route') {
            var r = routeById(id);
            var sub = r.Match.Path || (r.Match.Hosts.length ? r.Match.Hosts.join(', ') : S('map.anyHostPath'));
            li.querySelector('.item-sub').textContent = sub;
            li.querySelector('.item-chip').textContent = r.ClusterId || S('editor.noCluster');
            li.querySelector('.item-chip').classList.toggle('item-chip-warn', !r.ClusterId || !clusterById(r.ClusterId));
        } else {
            var c = clusterById(id);
            li.querySelector('.item-sub').textContent = Sn('editor.destCount', c._destRows.length) + ' · ' + (c.LoadBalancingPolicy || 'PowerOfTwoChoices');
        }
    }

    // ---------- save / reload / reset ----------

    function showErrors(errors) {
        var panel = document.getElementById('server-errors');
        panel.innerHTML = '<div class="error-title">' + icon('warn') + ' ' + esc(S('editor.errorsTitle')) + '</div>' +
            '<ul>' + errors.map(function (e2) { return '<li>' + esc(e2) + '</li>'; }).join('') + '</ul>';
        panel.classList.remove('hidden');
    }

    function hideErrors() {
        document.getElementById('server-errors').classList.add('hidden');
    }

    async function save() {
        if (loading) { return; }
        loading = true;
        updateDirtyState();
        var btn = document.getElementById('btn-save');
        btn.classList.add('loading');
        try {
            var doc = buildDoc();
            var res = await window.YarpUi.api('/api/yarp/config', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(doc)
            });
            if (res.ok) {
                var cfg = await res.json();
                applyConfig(cfg);
                hideErrors();
                refreshSelection();
                window.YarpUi.toast(S('editor.applied'), 'success');
                // If the host's config reload lands after the save response, quietly re-fetch
                // so the page settles on the latest settings (unless the user is editing again).
                setTimeout(function () {
                    if (!loading && !isDirty()) { reload(true); }
                }, 1200);
            } else {
                var err = {};
                try { err = await res.json(); } catch (e) { /* ignore */ }
                showErrors(err.errors || [S('editor.saveHttpError', res.status)]);
                window.YarpUi.toast(S('editor.validationFailed'), 'error');
            }
        } catch (err2) {
            window.YarpUi.toast(S('editor.saveFailed', err2.message), 'error');
        } finally {
            loading = false;
            btn.classList.remove('loading');
            updateDirtyState();
        }
    }

    async function reload(skipConfirm) {
        if (!skipConfirm && isDirty() && !window.confirm(S('editor.confirmDiscard'))) { return; }
        try {
            var res = await window.YarpUi.api('/api/yarp/config');
            if (!res.ok) { throw new Error('HTTP ' + res.status); }
            var cfg = await res.json();
            applyConfig(cfg);
            hideErrors();
            refreshSelection();
        } catch (err) {
            window.YarpUi.toast(S('editor.reloadFailed', err.message), 'error');
        }
    }

    async function resetToSeed() {
        var msg = attachMode ? S('editor.confirmRestore') : S('editor.confirmReset');
        if (!window.confirm(msg)) { return; }
        try {
            var res = await window.YarpUi.api('/api/yarp/config/reset', { method: 'POST' });
            if (res.ok) {
                var cfg = await res.json();
                applyConfig(cfg);
                refreshSelection();
                window.YarpUi.toast(attachMode ? S('editor.restoredFromBackup') : S('editor.resetToSeed'), 'success');
            } else {
                var err = {};
                try { err = await res.json(); } catch (e) { /* ignore */ }
                showErrors(err.errors || [S('editor.resetFailedShort')]);
            }
        } catch (err2) {
            window.YarpUi.toast(S('editor.resetFailed', err2.message), 'error');
        }
    }

    // ---------- boot ----------

    document.addEventListener('DOMContentLoaded', async function () {
        bindForm();

        document.getElementById('route-list').addEventListener('click', function (e) {
            var li = e.target.closest('.item');
            if (!li || !li.dataset.id) { return; }
            if (e.target.closest('.item-del')) { deleteRoute(li.dataset.id); return; }
            select('route', li.dataset.id);
        });

        document.getElementById('cluster-list').addEventListener('click', function (e) {
            var li = e.target.closest('.item');
            if (!li || !li.dataset.id) { return; }
            if (e.target.closest('.item-del')) { deleteCluster(li.dataset.id); return; }
            select('cluster', li.dataset.id);
        });

        document.getElementById('add-route').addEventListener('click', function () {
            var route = normalizeRoute({
                RouteId: uniqueName('new-route'),
                ClusterId: clusters.length ? clusters[0].ClusterId : '',
                Match: { Path: '/new-path/{**catch-all}' }
            });
            routes.push(route);
            editableRoutes.add(route.RouteId);
            select('route', route.RouteId);
            updateDirtyState();
        });

        document.getElementById('add-cluster').addEventListener('click', function () {
            var id = uniqueName('new-cluster');
            clusters.push(normalizeCluster({ ClusterId: id, Destinations: {} }));
            editableClusters.add(id);
            select('cluster', id);
            updateDirtyState();
        });

        document.getElementById('btn-save').addEventListener('click', save);
        document.getElementById('btn-reload').addEventListener('click', function () { reload(false); });
        document.getElementById('btn-reset').addEventListener('click', resetToSeed);

        window.addEventListener('beforeunload', function (e) {
            if (isDirty() && !loading) {
                e.preventDefault();
                e.returnValue = '';
            }
        });

        await reload(true);

        // Deep link from the map: /editor?select=route%3Amy-route
        var sel = new URLSearchParams(window.location.search).get('select');
        if (sel) {
            var type = sel.indexOf('cluster:') === 0 ? 'cluster' : (sel.indexOf('route:') === 0 ? 'route' : null);
            if (type) {
                var id = sel.substring(type.length + 1);
                var exists = type === 'route' ? !!routeById(id) : !!clusterById(id);
                if (exists) { select(type, id); }
            }
        }
    });
})();
