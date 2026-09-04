# YARP UI load test results

- **Date**: 2026-09-04 09:42
- **Upstream**: container (traefik/whoami:v1.12.0)
- **App under test**: in-process Kestrel — the exact `YARPUI.Host` pipeline (`AddYarpUi` + `UseYarpUiRequestLogging` + `MapYarpUi` + `MapReverseProxy`), request logging active
- **Machine**: Microsoft Windows 10.0.26200, 16 logical processors, 64 GiB RAM
- **Load model**: closed — NBomber `KeepConstant`, warm-up 15s, ramp 10s, sustain 60s per level (levels: 10, 25, 50)

| Scenario | Sessions | Req/s | p50 (ms) | p95 (ms) | p99 (ms) | Errors |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Direct GET (no proxy) | 10 | 13316 | 0.6 | 1.1 | 1.6 | 0 |
| Direct GET (no proxy) | 25 | 16291 | 1.1 | 3.3 | 4.7 | 0 |
| Direct GET (no proxy) | 50 | 16112 | 1.3 | 8.9 | 13.1 | 0 |
| Direct POST (no proxy) | 10 | 12920 | 0.6 | 1.2 | 1.6 | 0 |
| Direct POST (no proxy) | 25 | 15592 | 1.1 | 3.7 | 5.3 | 0 |
| Direct POST (no proxy) | 50 | 15811 | 1.2 | 9.7 | 14.4 | 0 |
| GET via YARP UI | 10 | 10109 | 0.8 | 1.4 | 1.8 | 0 |
| GET via YARP UI | 25 | 12640 | 1.5 | 3.9 | 5.6 | 0 |
| GET via YARP UI | 50 | 13363 | 1.9 | 10.2 | 14.8 | 0 |
| POST via YARP UI | 10 | 205 | 44.0 | 48.1 | 48.2 | 0 |
| POST via YARP UI | 25 | 507 | 44.1 | 48.2 | 48.4 | 0 |
| POST via YARP UI | 50 | 1006 | 44.2 | 48.3 | 48.8 | 0 |

## Notes

- **Zero errors** across all ~9 million requests this run generated (~8.95M ok, 0 failed), with request logging and the SQLite log writer active in the app under test.
- The load client (NBomber), the app under test and the upstream all ran on **one machine** — the direct-vs-proxy comparison therefore overstates proxy overhead (the client competes with the proxy for the same cores).
- **Proxied POST latency floor (~44 ms)**: the identical small-body POST sent straight to the upstream is fast (p95 1.2 ms at 10 sessions — see `direct_post`), so the stall comes from the proxy hop, not the client or container. YARP streams request bodies to the upstream; on this rig's Windows → WSL2 (podman) networking the streamed small body triggers a classic delayed-ACK stall (~40 ms timer). GET requests (no body) do not hit it. If POST latency matters for your topology, measure it there.
- Charts in this folder can be regenerated without a new run: `YARP_LOADTEST_RENDER_FROM=<reports dir>/results.json <loadtest exe> -method YARPASUI.LoadTests.LoadTestRuns.RenderChartsFromSavedResults`.
