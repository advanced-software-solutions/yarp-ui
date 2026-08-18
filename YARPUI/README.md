# YARP UI

A management UI for [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse Proxy). A single app that is **both** a working reverse proxy and its control room:

- **Route Map** (`/`) — every route → cluster → destination rendered as an interactive graph. Click a node to trace its full chain and inspect its configuration; search to highlight matches.
- **Editor** (`/editor`) — create, edit and delete routes, clusters and destinations. Saving validates the configuration, applies it to the running proxy **without a restart**, and persists it to disk.
- **Logs** (`/logs`) — a live view of proxied requests (method, path, status, duration, chosen destination), kept in memory.

> **Editions** — this repository is the **community edition**, free under Apache-2.0. A separate premium edition adds commercial features on top and is distributed under a commercial license. The premium code never lives in this repository.

## Hosting modes

The UI ships as a Razor Class Library (**YA-RP-UI** NuGet package) and can be hosted two ways:

**1. Standalone executable** — `YARPUI.Host` is a thin host that runs the proxy and the management UI in a single app:

```bash
cd YARPUI.Host && dotnet run      # → http://localhost:5080
```

**2. Embedded in your own app** — add the package and wire it up (see `samples/EmbeddedHost`):

```xml
<PackageReference Include="YA-RP-UI" Version="0.1.2" />
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

Under the hood the container sets `YarpUi__DataDirectory=/app/data` and mounts the volume there; an `appsettings.json` in that directory overrides the one baked into the image (this also works without Docker — point `YarpUi:DataDirectory` anywhere you like). To build the image manually: `docker build -t yarp-ui:0.1.0 .` from the solution root.

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

Only **proxied** requests are recorded (UI/API requests are excluded). Entries live in an in-memory ring buffer of the last 1000 requests and are lost on restart.

## Offline / no network

All JavaScript libraries (Cytoscape.js, dagre, cytoscape-dagre) are vendored under `wwwroot/lib/`. No CDN is used at runtime; the UI works fully offline.

## Security notes

- The management UI requires sign-in (cookie auth). **The proxy routes themselves are public** — that's the point of a proxy.
- Credentials sit in plain text in `appsettings.json`, which is fine for a local/internal tool. If you expose this app beyond localhost, put it behind HTTPS, use strong credentials, and consider extending the auth with hashed passwords or a real identity provider.
- Serve over HTTP only on a trusted network; the cookie is not marked `Secure` so it also works on plain HTTP during development.

## License

Copyright 2026 The YARP UI Authors.

Licensed under the [Apache License, Version 2.0](LICENSE). This is the community edition of YARP UI; the premium edition is licensed separately and distributed from its own repository.

"YARP UI" and the YARP UI logo are project trademarks; this license does not grant rights to use them to market derivative products.

Bundled third-party libraries (YARP, Cytoscape.js, dagre, cytoscape-dagre) are MIT-licensed — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
