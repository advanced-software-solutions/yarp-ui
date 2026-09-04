# YARP UI

A management UI for [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse Proxy). A single app that is **both** a working reverse proxy and its control room:

- **Route Map** (`/`) — every route → cluster → destination rendered as an interactive graph. Click a node to trace its full chain and inspect its configuration; search to highlight matches.
- **Editor** (`/editor`) — create, edit and delete routes, clusters and destinations. Saving validates the configuration, applies it to the running proxy **without a restart**, and persists it to disk.
- **Logs** (`/logs`) — proxied requests (method, path, status, duration, client IP, route, cluster, chosen destination), newest first with sortable columns. The table loads just the latest 10 entries and pages through the rest; all filters (route/cluster/destination, time frame, free text, status class) and sorting search the whole retained history server-side, not just what is loaded, and new entries stream onto the first page live. Plus a performance panel: per-request durations charted over time and colored by status class, avg/P95/max/error-rate stat cards, and per-route aggregates. Each row's client IP carries a one-click **block** action.
- **IP Blocking** (`/ipblocking`) — block a client IP, CIDR network or from–to range. Blocked requests are rejected with **403 before they reach the proxy** (or anything else the host serves). Rules apply immediately, are persisted across restarts, and blocked hits show up in the Logs page like any other request.

> **Editions** — this repository is the **community edition**, free under Apache-2.0. A separate premium edition adds commercial features on top and is distributed under a commercial license. The premium code never lives in this repository.

## Hosting modes

The UI ships as a Razor Class Library (**YA-RP-UI** NuGet package) and can be hosted two ways:

**1. Standalone executable** — `YARPUI.Host` is a thin host that runs the proxy and the management UI in a single app:

```bash
cd YARPUI.Host && dotnet run      # → http://localhost:5080
```

**2. Embedded in your own app** — add the package and wire it up (see `samples/EmbeddedHost`):

```xml
<PackageReference Include="YA-RP-UI" Version="0.2.0" />
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddYarpUi();               // proxy config, services, auth, Razor Pages

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();     // records proxied requests for the Logs page
app.MapYarpUi();                   // the UI pages + /api/yarp/*
app.MapReverseProxy();             // the proxy itself (public)
app.Run();
```

**3. Attached to an app that already configures YARP** — for gateways with their own `LoadFromConfig`/custom providers, transforms and filters. The UI shows the app's entire live configuration **and can edit it**: saving writes each change back into the `appsettings.json` file the route or cluster came from, and YARP hot-reloads the file — edits go live without a restart while the app's code (transforms, middleware, custom pipeline) stays untouched:

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(...);            // your custom work stays fully in charge

builder.AttachYarpUi();            // no proxy registration, no config seeding

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();
app.MapReverseProxy();
app.MapYarpUi();
```

How write-back editing behaves:

- **Edits are merged into the existing JSON nodes** — fields the editor doesn't model (e.g. `RateLimiterPolicy` or custom keys) keep their values; unrelated content in the file is preserved.
- **New routes/clusters** are added to `appsettings.json`; **deleted** ones are removed from every appsettings file that defines them (including environment overrides).
- **Backups**: the first time the UI modifies a file, a `.yarpui.bak` copy is kept next to it; *Restore appsettings backup* rolls every modified file back.
- Items that come from a **non-file source** (a custom `IProxyConfigProvider` backed by a database, code, etc.) are shown locked and read-only — there is no file to write back to.
- Pre-existing config quirks (e.g. a route referencing a missing cluster) don't block saves; only problems the edit itself introduces are rejected.

All modes read the same configuration (`YarpUi:Auth` credentials) and support `YarpUi:DataDirectory` for volume-backed persistence. The UI authenticates with its own cookie scheme (`YarpUi.Auth`) and never changes the host's default authentication scheme, so it is safe next to an app's existing JWT/cookie setup.

## Quick start

```bash
dotnet run
```

Open http://localhost:5080 and sign in. Default credentials (change them!):

| Setting | Value |
| --- | --- |
| Username | `admin` |
| Password | `yarp-admin` |

Both are configured in `appsettings.json` under `YarpUi:Auth`.

## Docker

A template `docker-compose.yml` ships next to the solution:

```bash
docker compose up -d --build
```

The UI is then served on **http://localhost:8090**. All mutable configuration is volume-persisted in `./docker-data` so it survives `docker compose down`:

| File | Purpose |
| --- | --- |
| `docker-data/appsettings.json` | Credentials (`YarpUi:Auth`) and the seed `ReverseProxy` config — edit on the host, applies on next start |
| `docker-data/yarp-ui.routes.json` | Written automatically on every save from the UI editor |
| `docker-data/yarp-ui-ipblocklist.json` | IP block list (rules + settings) — written on every change from the IP Blocking page |
| `docker-data/yarp-ui-logs.db` | Request log database (SQLite) — survives restarts, purged by the retention policy |

Under the hood the container sets `YarpUi__DataDirectory=/app/data` and mounts the volume there; an `appsettings.json` in that directory overrides the one baked into the image (this also works without Docker — point `YarpUi:DataDirectory` anywhere you like). To build the image manually: `docker build -t yarp-ui:0.4.0 .` from the solution root.

## IIS

Hosting under IIS works with the default application pool identity (`ApplicationPoolIdentity`), which has **read-only** access to the site folder. On startup YARP UI detects that the content root is not writable and stores all mutable state — `yarp-ui-logs.db`, `yarp-ui.routes.json`, the optional data-directory `appsettings.json` — under `%ProgramData%\YarpUi\<application name>` instead, logging a warning so the relocation is visible. Nothing needs to be configured; the proxy starts normally and the editor/logs pages work against the fallback folder.

To keep state in a location of your choosing instead, either point `YarpUi:DataDirectory` at a writable folder, or grant the pool identity write access to the site folder:

```powershell
icacls "<site folder>" /grant "IIS AppPool\<YourAppPool>:(OI)(CI)(M)"
```

An explicitly configured `YarpUi:DataDirectory` is never overridden by the fallback. The fallback location itself can be redirected with `YarpUi:FallbackDataDirectory`.

## How configuration works

```
appsettings.json ("ReverseProxy" section)   ← hand-written seed
                │
                ▼  startup
   yarp-ui.routes.json (if present)         ← takes precedence once it exists
                │
                ▼
   InMemoryConfigProvider (live YARP config)
```

- On startup the app loads `yarp-ui.routes.json` if it exists; otherwise it reads the `ReverseProxy` section from `appsettings.json`.
- The first **Save** in the editor writes the full configuration to `yarp-ui.routes.json`. From that point on, that file is the source of truth — `appsettings.json` is left untouched.
- **Reset to appsettings.json** (editor, bottom-left) deletes the UI-managed file and returns to the seed configuration.
- Saves are validated with YARP's own config validator; invalid configurations are rejected and the proxy keeps running with the last good config.

## Request logs

Only **proxied** requests are recorded (UI/API requests are excluded). Entries are stored in a SQLite database (`yarp-ui-logs.db` in the data directory, next to `yarp-ui.routes.json`) and survive restarts. Each entry captures the method, path, status code, duration, the route/cluster/destination YARP selected, and the client IP. Databases created by older versions are migrated in place on first start.

The **client IP** is the leftmost `X-Forwarded-For` entry when a fronting proxy supplied one, otherwise the direct connection address. The UI does not install the ForwardedHeaders middleware itself — if the whole app sits behind a load balancer, the header reflects what that proxy forwarded. Since `X-Forwarded-For` is caller-controlled, treat logged IPs as informational rather than authenticated.

The Logs page loads **only the latest page of entries** (10 rows, newest first) and pages through the rest on demand, so opening it stays fast no matter how much history is retained. All filtering and sorting run **server-side over the entire retained history** via `GET /api/yarp/logs` with `from`/`to` (Unix milliseconds), `routeId`, `clusterId`, `destinationId`, `q` (free text over path, method, route/cluster/destination and client IP), `status` (status class 2–5), `sort`, `desc`, `limit` (max 1000 per query) and `offset` for paging; the response reports the total match count. Without search parameters the endpoint keeps its live-tailing contract: `after=<seq>` streams new entries oldest-first. The page keeps the first page fresh live (new entries appear at the top while Live is on); deeper pages stay stable while you browse them.

A **retention policy** deletes logs automatically once they pass a certain age: a background task runs at startup and then every hour. The policy is managed from the Logs page toolbar (*Keep logs: forever / 1 / 7 / 30 / 90 / 365 days*) and changing it applies immediately; the initial default comes from `YarpUi:Logs:RetentionDays` in configuration (30 days if unset). The policy you set in the UI is stored in the database itself and wins over the configuration value.

## IP blocking

The **IP Blocking** page (`/ipblocking`) blocks abusive clients at the front door. YARP itself has no client-IP access control, so YARP UI adds it: a middleware that rejects blocked addresses with **403 before the request reaches routing, the proxy or anything else the host serves**. It is enabled in every hosting mode (standalone, embedded and attach) without any host code change — the package inserts it the same way it inserts its localization middleware. With an empty list it costs effectively nothing; with rules loaded, matching is a precompiled hash lookup / binary search with no locks or allocations per request, and adding or removing a rule swaps the compiled list atomically (no restart, no dropped requests).

Rules accept three notations and apply to both IPv4 and IPv6:

| Notation | Example |
| --- | --- |
| Single address | `203.0.113.7` |
| CIDR network (host bits must be zero) | `203.0.113.0/24` |
| Inclusive from–to range | `203.0.113.5-203.0.113.99` |

Behavior details:

- **What gets blocked**: every request **except the management UI itself** — its pages, `/api/yarp/*` and its static assets stay reachable no matter what, so an admin can never lock themselves out; a too-wide rule is always removable from the UI (or by deleting `yarp-ui-ipblocklist.json` in the data directory). In attach mode the block also covers the host application's own routes, since the check runs before routing.
- **Which address is matched**: the direct connection address (`Connection.RemoteIpAddress`), which is unspoofable and correct when YARP UI is the edge proxy. If the whole app sits behind another trusted proxy or load balancer, enable **Honor X-Forwarded-For** on the page to match the leftmost `X-Forwarded-For` entry instead. That header is caller-controlled: only enable the toggle when direct clients cannot reach the app, otherwise an attacker can spoof the header to evade (or trigger) blocks.
- **Persistence**: rules and the toggle live in `yarp-ui-ipblocklist.json` in the data directory (next to `yarp-ui.routes.json`), written atomically on every change and reloaded on restart. A corrupt file never takes the app down — it falls back to an empty list with a warning; individual rules that no longer parse are skipped.
- **Visibility**: every blocked request is written to the request log (status 403, the matching rule named in the error field, the client IP), so blocks are searchable on the Logs page like any other traffic. The Logs page also has a one-click **block** button on each row's client IP.
- **API**: `GET /api/yarp/ipblocking`, `POST /api/yarp/ipblocking/rules`, `DELETE /api/yarp/ipblocking/rules/{id}`, `PUT /api/yarp/ipblocking/settings`, and `POST /api/yarp/ipblocking/check` (reports which rule an address would hit — the page's *Test an address* box).
- The list is capped at 1000 rules; overlapping ranges are merged internally (the request is blocked either way, the log names one of the matching rules).

## Localization

The UI ships in **English** (default), **Arabic** (rendered right-to-left), **Spanish** and **Simplified Chinese**. A request's culture is resolved in this order: the `?culture=` query string, the standard ASP.NET Core culture cookie, the browser's `Accept-Language` header, then the default. The language switcher in the top bar (and on the login page) writes that cookie and reloads.

No host wiring is required: the package inserts its own request-localization middleware scoped to the UI's routes only (`/login`, the UI pages, `/api/yarp/*`), so host applications never need to call `UseRequestLocalization` and their own pages keep whatever culture behavior they had.

Two settings control the language set (in `appsettings.json`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `YarpUi:Cultures` | `en,ar,es,zh-Hans,zh-CN` | Comma-separated cultures the UI may respond in |
| `YarpUi:DefaultCulture` | `en` | Culture used when a request doesn't match any supported one |

`zh-CN` is accepted as an alias for `zh-Hans` (browsers send the regional tag); unsupported cultures fall back to the default. Validation errors — both from the management API and from the editor's configuration checks — are localized with the same request culture.

## Offline / no network

All JavaScript libraries (Cytoscape.js, dagre, cytoscape-dagre, Chart.js) are vendored under `wwwroot/lib/`. No CDN is used at runtime; the UI works fully offline.

## Security notes

- The management UI requires sign-in (cookie auth). **The proxy routes themselves are public** — that's the point of a proxy. Use the IP Blocking page to reject abusive clients (see above); the UI surface itself is deliberately exempt from the block list.
- Credentials sit in plain text in `appsettings.json`, which is fine for a local/internal tool. If you expose this app beyond localhost, put it behind HTTPS, use strong credentials, and consider extending the auth with hashed passwords or a real identity provider.
- Serve over HTTP only on a trusted network; the cookie is not marked `Secure` so it also works on plain HTTP during development.

## License

Copyright 2026 The YARP UI Authors.

Licensed under the [Apache License, Version 2.0](LICENSE). This is the community edition of YARP UI; the premium edition is licensed separately and distributed from its own repository.

"YARP UI" and the YARP UI logo are project trademarks; this license does not grant rights to use them to market derivative products.

Bundled third-party libraries (YARP, Microsoft.Data.Sqlite, Cytoscape.js, dagre, cytoscape-dagre, Chart.js) are MIT-licensed — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
