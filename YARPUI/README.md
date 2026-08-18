# YARP UI

A management UI for [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse Proxy). A single app that is **both** a working reverse proxy and its control room:

- **Route Map** (`/`) — every route → cluster → destination rendered as an interactive graph. Click a node to trace its full chain and inspect its configuration; search to highlight matches.
- **Editor** (`/editor`) — create, edit and delete routes, clusters and destinations. Saving validates the configuration, applies it to the running proxy **without a restart**, and persists it to disk.
- **Logs** (`/logs`) — a live view of proxied requests (method, path, status, duration, chosen destination), kept in memory.

> **Editions** — this repository is the **community edition**, free under Apache-2.0. A separate premium edition adds commercial features on top and is distributed under a commercial license. The premium code never lives in this repository.

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
