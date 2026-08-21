/* Route map: renders the live YARP configuration as an interactive route → cluster → destination graph. */
(function () {
    'use strict';

    var esc = window.YarpUi.esc;
    var icon = window.YarpUi.icon;
    var S = window.YarpUi.S;
    var Sn = window.YarpUi.Sn;
    var cy = null;
    var config = null;
    var selectedId = null;
    var searchMatches = null;
    var flowFrame = null;
    var flowOffset = 0;

    var TYPE_META = {
        route: { cls: 'route', label: S('map.typeRoute'), w: 218, h: 64 },
        cluster: { cls: 'cluster', label: S('map.typeCluster'), w: 202, h: 64 },
        dest: { cls: 'dest', label: S('map.typeDestination'), w: 234, h: 64 }
    };

    function nodeId(type, id) { return type + ':' + id; }

    function routeSub(route) {
        if (route.Match && route.Match.Path) { return route.Match.Path; }
        if (route.Match && route.Match.Hosts && route.Match.Hosts.length) { return route.Match.Hosts.join(', '); }
        return S('map.anyHostPath');
    }

    function buildElements(cfg) {
        var elements = [];
        var routes = cfg.Routes || [];
        var clusters = cfg.Clusters || [];
        var clusterIds = {};
        var editableRoutes = cfg.EditableRouteIds || null;
        var editableClusters = cfg.EditableClusterIds || null;
        var isExternalCluster = function (id) { return editableClusters !== null && editableClusters.indexOf(id) === -1; };

        clusters.forEach(function (c) {
            clusterIds[c.ClusterId] = true;
            elements.push({
                group: 'nodes',
                data: { id: nodeId('cluster', c.ClusterId), type: 'cluster', label: c.ClusterId, sub: c.LoadBalancingPolicy || S('map.lbDefault'), external: isExternalCluster(c.ClusterId) }
            });
            Object.keys(c.Destinations || {}).forEach(function (name) {
                var address = (c.Destinations[name] && c.Destinations[name].Address) || '';
                elements.push({
                    group: 'nodes',
                    data: { id: nodeId('dest', c.ClusterId + '|' + name), type: 'dest', label: name, sub: address, external: isExternalCluster(c.ClusterId) }
                });
                elements.push({
                    group: 'edges',
                    data: { id: 'cd:' + c.ClusterId + '|' + name, edgeType: 'cd', source: nodeId('cluster', c.ClusterId), target: nodeId('dest', c.ClusterId + '|' + name) }
                });
            });
        });

        routes.forEach(function (r) {
            var external = editableRoutes !== null && editableRoutes.indexOf(r.RouteId) === -1;
            elements.push({
                group: 'nodes',
                data: {
                    id: nodeId('route', r.RouteId), type: 'route', label: r.RouteId, sub: routeSub(r),
                    methods: (r.Match && r.Match.Methods) || null,
                    brokenCluster: !r.ClusterId || !clusterIds[r.ClusterId],
                    external: external
                }
            });
            if (r.ClusterId && clusterIds[r.ClusterId]) {
                elements.push({
                    group: 'edges',
                    data: { id: 'rc:' + r.RouteId, edgeType: 'rc', source: nodeId('route', r.RouteId), target: nodeId('cluster', r.ClusterId) }
                });
            }
        });

        return elements;
    }

    function cardHtml(node) {
        var d = node.data();
        var meta = TYPE_META[d.type];
        var methods = '';
        if (d.methods && d.methods.length) {
            methods = '<span class="ncard-badge">' + esc(d.methods.join(' ')) + '</span>';
        }
        var warn = d.type === 'route' && d.brokenCluster
            ? '<span class="ncard-warn" title="' + esc(S('map.clusterNotFound')) + '">' + icon('warn') + '</span>'
            : '';
        // The "external" badge text lives in a data attribute the CSS renders via ::after,
        // so it can be localized while staying out of the layout math.
        var externalAttr = d.external ? ' data-external="' + esc(S('map.externalBadge')) + '"' : '';
        return '' +
            '<div class="ncard ncard-' + meta.cls + (d.external ? ' ncard-external' : '') + '" data-nodeid="' + esc(d.id) + '" data-type="' + d.type + '"' +
                (d.external ? ' title="' + esc(S('map.externalSource')) + '"' : '') + '>' +
                '<span class="ncard-icon">' + icon(d.type === 'dest' ? 'dest' : d.type) + '</span>' +
                '<span class="ncard-text">' +
                    '<span class="ncard-title"' + externalAttr + '>' + esc(d.label) + '</span>' +
                    '<span class="ncard-sub" title="' + esc(d.sub) + '">' + esc(d.sub) + '</span>' +
                '</span>' +
                methods + warn +
            '</div>';
    }

    function cardEl(cyId) {
        return document.querySelector('.ncard[data-nodeid="' + (window.CSS && CSS.escape ? CSS.escape(cyId) : cyId) + '"]');
    }

    // Card layer: plain divs positioned over the invisible canvas nodes, synced on every render.
    function ensureCardLayer() {
        var container = document.getElementById('cy');
        var existing = container.querySelector('.ncard-layer');
        if (existing) { existing.remove(); }
        var layer = document.createElement('div');
        layer.className = 'ncard-layer';
        container.appendChild(layer);
        return layer;
    }

    function buildCards() {
        var layer = document.querySelector('.ncard-layer');
        if (!layer || !cy) { return; }
        var html = '';
        cy.nodes().forEach(function (n) { html += cardHtml(n); });
        layer.innerHTML = html;
        syncCards();
    }

    function syncCards() {
        if (!cy) { return; }
        cy.nodes().forEach(function (n) {
            var el = cardEl(n.id());
            if (!el) { return; }
            var p = n.renderedPosition();
            if (!p || !isFinite(p.x) || !isFinite(p.y)) { return; }
            el.style.transform = 'translate(' + Math.round(p.x - el.offsetWidth / 2) + 'px, ' + Math.round(p.y - el.offsetHeight / 2) + 'px)';
        });
    }

    function connectedComponent(node) {
        var found = cy.collection(node);
        var frontier = cy.collection(node);
        while (frontier.length) {
            var next = frontier.neighborhood().filter(function (el) {
                return !found.contains(el);
            });
            found = found.union(next);
            frontier = next.filter('node');
        }
        return found;
    }

    function refreshHighlight() {
        cy.elements().removeClass('dim hl');
        document.querySelectorAll('.ncard').forEach(function (el) {
            el.classList.remove('dimmed', 'selected', 'match');
        });

        if (searchMatches !== null) {
            cy.elements().addClass('dim');
            searchMatches.removeClass('dim');
            searchMatches.nodes().forEach(function (n) {
                var el = cardEl(n.id());
                if (el) { el.classList.add('match'); }
            });
            stopFlow();
            return;
        }

        if (selectedId === null) {
            stopFlow();
            return;
        }

        var node = cy.getElementById(selectedId);
        if (node.empty()) { return; }
        var comp = connectedComponent(node);
        cy.elements().addClass('dim');
        comp.removeClass('dim');
        comp.edgesWith(comp).addClass('hl');
        var compIds = {};
        comp.nodes().forEach(function (n) { compIds[n.id()] = true; });
        cy.nodes().forEach(function (n) {
            var el = cardEl(n.id());
            if (!el) { return; }
            if (!compIds[n.id()]) {
                el.classList.add('dimmed');
            } else if (n.id() === selectedId) {
                el.classList.add('selected');
            }
        });
        startFlow();
    }

    function startFlow() {
        if (flowFrame !== null) { return; }
        (function tick() {
            flowOffset -= 0.9;
            cy.edges('.hl').style('line-dash-offset', flowOffset);
            flowFrame = requestAnimationFrame(tick);
        })();
    }

    function stopFlow() {
        if (flowFrame !== null) {
            cancelAnimationFrame(flowFrame);
            flowFrame = null;
        }
    }

    function findConfigObject(nodeIdStr) {
        var parts = nodeIdStr.split(':');
        var type = parts[0], id = parts.slice(1).join(':');
        if (type === 'route') {
            return (config.Routes || []).find(function (r) { return r.RouteId === id; }) || null;
        }
        if (type === 'cluster') {
            return (config.Clusters || []).find(function (c) { return c.ClusterId === id; }) || null;
        }
        if (type === 'dest') {
            var bar = id.indexOf('|');
            var clusterId = id.substring(0, bar), name = id.substring(bar + 1);
            var cluster = (config.Clusters || []).find(function (c) { return c.ClusterId === clusterId; });
            var dest = cluster && cluster.Destinations ? cluster.Destinations[name] : null;
            return dest ? { Name: name, Cluster: clusterId, Address: dest.Address, Health: dest.Health || null } : null;
        }
        return null;
    }

    function kvRow(key, value) {
        if (value === null || value === undefined || value === '') { return ''; }
        return '<div class="kv"><span class="kv-key">' + esc(key) + '</span><span class="kv-value">' + esc(value) + '</span></div>';
    }

    function showDrawer(nodeIdStr) {
        var node = cy.getElementById(nodeIdStr);
        if (node.empty()) { return; }
        var d = node.data();
        var meta = TYPE_META[d.type];
        var obj = findConfigObject(nodeIdStr) || {};

        var title = document.getElementById('drawer-title');
        title.innerHTML = '<span class="drawer-icon ncard-icon ncard-icon-' + meta.cls + '">' + icon(d.type === 'dest' ? 'dest' : d.type) + '</span>' +
            '<span class="drawer-name">' + esc(d.label) + '</span>' +
            '<span class="type-chip type-' + meta.cls + '">' + meta.label + '</span>';

        var body = document.getElementById('drawer-body');
        var rows = '';
        if (d.type === 'route') {
            var r = obj;
            rows += kvRow(S('map.kvCluster'), r.ClusterId);
            rows += kvRow(S('map.kvPath'), r.Match && r.Match.Path);
            rows += kvRow(S('map.kvHosts'), r.Match && r.Match.Hosts ? r.Match.Hosts.join(', ') : null);
            rows += kvRow(S('map.kvMethods'), r.Match && r.Match.Methods ? r.Match.Methods.join(', ') : S('map.any'));
            rows += kvRow(S('map.kvOrder'), r.Order);
            rows += kvRow(S('map.kvAuthorization'), r.AuthorizationPolicy);
            rows += kvRow(S('map.kvCors'), r.CorsPolicy);
            rows += kvRow(S('map.kvTransforms'), r.Transforms ? S('map.nConfigured', r.Transforms.length) : null);
        } else if (d.type === 'cluster') {
            var c = obj;
            rows += kvRow(S('map.kvLoadBalancing'), c.LoadBalancingPolicy || S('map.lbDefault'));
            rows += kvRow(S('map.kvDestinations'), c.Destinations ? Object.keys(c.Destinations).length + '' : '0');
            rows += kvRow(S('map.kvActiveHealth'), c.HealthCheck && c.HealthCheck.Active && c.HealthCheck.Active.Enabled ? S('map.enabled') : null);
            rows += kvRow(S('map.kvPassiveHealth'), c.HealthCheck && c.HealthCheck.Passive && c.HealthCheck.Passive.Enabled ? S('map.enabled') : null);
        } else {
            rows += kvRow(S('map.kvAddress'), obj.Address);
            rows += kvRow(S('map.kvCluster'), obj.Cluster);
            rows += kvRow(S('map.kvName'), obj.Name);
        }

        body.innerHTML =
            '<div class="kv-list">' + rows + '</div>' +
            '<div class="json-block"><pre class="json">' + window.YarpUi.prettyJson(obj) + '</pre></div>';

        var editLink = document.getElementById('drawer-edit');
        editLink.href = '/editor?select=' + encodeURIComponent(nodeIdStr);

        var drawer = document.getElementById('drawer');
        drawer.classList.add('open');
        drawer.setAttribute('aria-hidden', 'false');
    }

    function hideDrawer() {
        var drawer = document.getElementById('drawer');
        drawer.classList.remove('open');
        drawer.setAttribute('aria-hidden', 'true');
    }

    function renderLegend(cfg) {
        var legend = document.getElementById('map-legend');
        var routes = (cfg.Routes || []).length;
        var clusters = (cfg.Clusters || []).length;
        var dests = (cfg.Clusters || []).reduce(function (sum, c) {
            return sum + Object.keys(c.Destinations || {}).length;
        }, 0);
        legend.innerHTML =
            '<span class="legend-chip legend-route"><span class="legend-dot"></span>' + Sn('map.legendRoutes', routes) + '</span>' +
            '<span class="legend-chip legend-cluster"><span class="legend-dot"></span>' + Sn('map.legendClusters', clusters) + '</span>' +
            '<span class="legend-chip legend-dest"><span class="legend-dot"></span>' + Sn('map.legendDestinations', dests) + '</span>';
    }

    function initGraph(cfg) {
        var container = document.getElementById('cy');
        if (cy) {
            cy.destroy();
            cy = null;
        }
        ensureCardLayer();

        cy = cytoscape({
            container: container,
            elements: buildElements(cfg),
            wheelSensitivity: 0.25,
            minZoom: 0.2,
            maxZoom: 2.5,
            boxSelectionEnabled: false,
            layout: {
                name: 'dagre',
                rankDir: 'LR',
                nodeSep: 48,
                rankSep: 120,
                animate: true,
                animationDuration: 420
            },
            style: [
                {
                    selector: 'node',
                    style: {
                        'background-opacity': 0,
                        'border-width': 0,
                        'z-index': 10
                    }
                },
                { selector: 'node[type="route"]', style: { width: TYPE_META.route.w, height: TYPE_META.route.h } },
                { selector: 'node[type="cluster"]', style: { width: TYPE_META.cluster.w, height: TYPE_META.cluster.h } },
                { selector: 'node[type="dest"]', style: { width: TYPE_META.dest.w, height: TYPE_META.dest.h } },
                {
                    selector: 'edge',
                    style: {
                        'curve-style': 'bezier',
                        'line-color': 'rgba(125, 146, 184, 0.34)',
                        'target-arrow-shape': 'triangle',
                        'target-arrow-color': 'rgba(125, 146, 184, 0.34)',
                        'arrow-scale': 0.6,
                        width: 1.6
                    }
                },
                { selector: 'edge[edgeType="rc"]', style: { 'line-color': 'rgba(96, 165, 250, 0.4)', 'target-arrow-color': 'rgba(96, 165, 250, 0.4)' } },
                { selector: 'edge[edgeType="cd"]', style: { 'line-color': 'rgba(74, 222, 128, 0.34)', 'target-arrow-color': 'rgba(74, 222, 128, 0.34)' } },
                { selector: 'edge.dim', style: { opacity: 0.1 } },
                { selector: 'edge.hl', style: { width: 2.4, 'line-dash-pattern': [7, 6] } },
                { selector: 'edge.hl[edgeType="rc"]', style: { 'line-color': 'rgba(96, 165, 250, 0.95)', 'target-arrow-color': 'rgba(96, 165, 250, 0.95)' } },
                { selector: 'edge.hl[edgeType="cd"]', style: { 'line-color': 'rgba(74, 222, 128, 0.9)', 'target-arrow-color': 'rgba(74, 222, 128, 0.9)' } }
            ]
        });

        cy.on('layoutstop', function () {
            buildCards();
            cy.fit(undefined, 70);
            syncCards();
        });

        cy.on('render', syncCards);
        cy.ready(function () { buildCards(); });

        cy.on('tap', 'node', function (e) {
            selectedId = e.target.id();
            searchMatches = null;
            hideSearchCount();
            refreshHighlight();
            showDrawer(selectedId);
            document.getElementById('map-hint').classList.add('faded');
        });

        cy.on('tap', function (e) {
            if (e.target === cy) {
                selectedId = null;
                refreshHighlight();
                hideDrawer();
            }
        });

        cy.on('mouseover', 'node', function (e) {
            var el = cardEl(e.target.id());
            if (el) { el.classList.add('hover'); }
        });
        cy.on('mouseout', 'node', function (e) {
            var el = cardEl(e.target.id());
            if (el) { el.classList.remove('hover'); }
        });

        selectedId = null;
        searchMatches = null;
    }

    function hideSearchCount() {
        var el = document.getElementById('search-count');
        el.classList.add('hidden');
        el.textContent = '';
    }

    function applySearch(rawQuery) {
        var q = rawQuery.trim().toLowerCase();
        if (!q) {
            searchMatches = null;
            hideSearchCount();
            refreshHighlight();
            return;
        }
        searchMatches = cy.nodes().filter(function (n) {
            var d = n.data();
            return (d.label || '').toLowerCase().indexOf(q) !== -1 ||
                   (d.sub || '').toLowerCase().indexOf(q) !== -1;
        });
        var count = searchMatches.length;
        var counter = document.getElementById('search-count');
        counter.textContent = count === 0 ? S('map.noMatch') : Sn('map.match', count);
        counter.classList.remove('hidden');
        selectedId = null;
        hideDrawer();
        refreshHighlight();
    }

    async function load(showSpinner) {
        var status = document.getElementById('map-status');
        if (showSpinner) { status.classList.remove('hidden'); }
        try {
            var res = await window.YarpUi.api('/api/yarp/config');
            if (!res.ok) { throw new Error('HTTP ' + res.status); }
            config = await res.json();
            window.YarpUi.setSourcePill(config.ManagedByUi);
            renderLegend(config);
            var empty = (!config.Routes || !config.Routes.length) && (!config.Clusters || !config.Clusters.length);
            document.getElementById('map-empty').classList.toggle('hidden', !empty);
            document.getElementById('cy').style.visibility = empty ? 'hidden' : 'visible';
            if (!empty) {
                initGraph(config);
            } else if (cy) {
                cy.destroy();
                cy = null;
            }
        } catch (err) {
            window.YarpUi.toast(S('map.loadFailed', err.message), 'error');
        } finally {
            status.classList.add('hidden');
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.getElementById('map-search').addEventListener('input', window.YarpUi.debounce(function (e) {
            if (!cy) { return; }
            applySearch(e.target.value);
        }, 160));

        document.getElementById('search-count').addEventListener('click', function () {
            document.getElementById('map-search').value = '';
            applySearch('');
        });

        document.getElementById('drawer-close').addEventListener('click', function () {
            selectedId = null;
            refreshHighlight();
            hideDrawer();
        });

        document.getElementById('btn-fit').addEventListener('click', function () {
            if (cy) { cy.fit(undefined, 70); }
        });
        document.getElementById('btn-zoom-in').addEventListener('click', function () {
            if (!cy) { return; }
            cy.zoom({ level: cy.zoom() * 1.25, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
        });
        document.getElementById('btn-zoom-out').addEventListener('click', function () {
            if (!cy) { return; }
            cy.zoom({ level: cy.zoom() / 1.25, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
        });
        document.getElementById('btn-refresh').addEventListener('click', function () {
            load(true).then(function () { window.YarpUi.toast(S('map.reloaded'), 'success'); });
        });

        load(true);
    });
})();
