# Devlog

Decision record for this fork. Newest entries first.

## 2026-08-30 — ClickStack trial scaffolding (`bhcs/`, separate app)

- **Trialing ClickStack for app-side observability** (exceptions + session replay, browser
  network capture, one SDK for devs) as a **separate app `bhcs`** — this stack is untouched
  and keeps platform telemetry (NATS, PromQL dashboards). Rollback = destroy the app.
- **No fork.** `bhcs/Dockerfile` is packaging-only over the pinned upstream all-in-one
  image. Note: images migrated from `docker.hyperdx.io/hyperdx/*` to Docker Hub
  `clickhouse/clickstack-*`; pinned `clickhouse/clickstack-all-in-one:2.37.0` (newest
  release tag at pin time). ClickHouse releases monthly — changelog read before every bump.
- **Single 8GB machine, single volume** (the bhgrafana posture; ClickStack's compose docs
  bless single-server/no-fault-tolerance production). One volume at `/data`: Mongo already
  writes `/data/db`; ClickHouse moved beside it via `config.d` `<path>` override (image
  default `/var/lib/clickhouse` is ephemeral). `kill_timeout=120s` for clean CH shutdowns.
- **Memory governance so querying can never kill ingestion** (the defaults are mutually
  suicidal: CH assumes 90% of RAM, WiredTiger 50%): server capped at 4GB absolute,
  per-query 3GB with spill-to-disk at 1.5GB (external GROUP BY/sort on the volume,
  `grace_hash` joins), `max_threads=4`, `max_execution_time=300`, Node processes capped
  via `NODE_OPTIONS`. Mongo cache left default — metadata is tiny; on the watch list.
  Failure mode by design: oversized queries run slow or error; the server never OOMs.
- **Private-first (flycast only), same doctrine as here**: UI 8080 + API 8000 + OTLP
  4317/4318 over flycast; no public IPs in phase 1. Replay testing works for mesh-connected
  browsers. Real-player RUM later requires a public IP — a deliberate doctrine change
  (auth boundary becomes HyperDX login + ingestion API key), with replay masking
  (`maskAllInputs`/`maskAllText`) mandatory before any real traffic: HyperDX defaults are
  permissive, the opposite of Sentry's.
- **Trial ingestion is SDK-only; NATS stays here.** SDK vs NATS is not either/or — they
  carry different data (app telemetry vs platform telemetry about machines/proxies that no
  SDK can see). Phase 2, only if the trial convinces: dual-write platform logs from this
  app's Vector into bhcs (snippet in `bhcs/README.md`; needs CH `listen_host ::` + a
  password-protected ingest user first). Metrics last — no PromQL in ClickStack yet, so
  the fly dashboards submodule stays authoritative here until that lands upstream.
- Runbook with verification tripwires (config-applied checks, OTLP smoke, **persistence
  test across restart+redeploy**, no-public-IP check) in `bhcs/README.md`.

## 2026-08-28 — Moved to the org's `production` network (destroy + recreate)

- **The org's apps live on a custom Fly network named `production`** (`fdaa:74:505b::/48`).
  Creating bhgrafana without `--network` put it on the org *default* network
  (`fdaa:c:458c::/48`) — networks are mutually isolated (no DNS, no routing), so the team's
  mesh couldn't reach the UI and services could never have sent traces to it. Networks are
  fixed at app creation → destroyed and recreated with
  `fly apps create bhgrafana --org beehive-gaming --network production`.
  **Always check the network column in `fly apps list` before creating apps in this org.**
- **`fly deploy --flycast` allocates its private IP on the DEFAULT network** even when the
  app is on a custom network. Fix: `fly ips release <ip>`, then
  `fly ips allocate-v6 --private --network production -a bhgrafana`. Verify the flycast
  prefix matches the machines' 6PN prefix in `fly ips list`.
- ACCESS_TOKEN was carried over in-memory (same token, never displayed). The old volume's
  ~2h of telemetry was not preserved — Fly volumes are app-bound (no cross-app
  detach/attach); if data ever matters during a future move, evaluate snapshot-restore
  first (daily snapshots are enabled).
- Re-verified after the move: ingestion resumed immediately (~320k log rows/5min,
  ~17.5k `fly_*` series), UI 200 via `fdaa:74:505b:0:1::a`, `bo-api-casino.flycast`
  resolves from inside the app (same-network proof), OTLP smoke span ingested through
  `bhgrafana.flycast:10428` and read back via the Tempo API.

## 2026-08-28 — First deploy findings (bhgrafana, sin, beehive-gaming)

- **NATS "authorization violation" root cause: the floating `timberio/vector:latest` tag.**
  The unpinned tag pulled vector 0.58.0 (built two days before our deploy), whose NATS client
  is rejected by Fly's NATS proxy with credentials that are provably valid — a raw NATS
  handshake from inside the machine (`CONNECT`/`SUB logs.>`) authenticated and streamed org
  logs with the exact same user/token. Verified in-machine with the template's own config:
  vector **0.46.1 works** (upstream-era version), **0.57.0 and 0.58.0 fail**. Pinned
  `timberio/vector:0.46.1-distroless-static`; bump only with a changelog read and an
  in-machine NATS test. (The token itself was fine all along — `readonly` org tokens are the
  documented type for the platform streams.)
- **Vector exits permanently when a source fails at topology build** — and since Grafana is
  the container's foreground process, the machine looks healthy while ingestion is dead.
  Fix: `vector.sh` now supervises vector in a retry loop.
- **Grafana `main` image clamps anonymous access to Viewer.** Grafana 12.2 logs
  `auth.anonymous.org_role is deprecated, only viewer role is supported` — the template's
  anonymous-Admin assumption no longer holds. Dashboards/datasources are file-provisioned so
  day-to-day viewing and Explore still work; interactive dashboard *editing* needs an admin
  login (`GF_SECURITY_ADMIN_PASSWORD`) or pinning an older Grafana. Decide when dashboard
  work starts in earnest.
- Volume `data` (10GB, sin) was created with scheduled daily snapshots on by default
  (5-day retention) — plan step 2.4 satisfied without extra work.
- **Grafana moved from port 3000 to 80** (grafana.ini `http_port`, fly.toml `internal_port`,
  vector self-scrape endpoint): the UI is port-free on 6PN (`http://bhgrafana.internal/`) and
  the Flycast mapping is a same-number 80 → 80, ending the 3000-vs-80 confusion. Laptop access:
  `fly proxy 3000:80 -a bhgrafana` → http://localhost:3000 (binding local 80 would need sudo).
- **Verified end-to-end after the vector pin**: ~155k log rows/5min in VictoriaLogs and
  ~16.6k `fly_*` platform metric series in VictoriaMetrics (org streams flowing); self-scrape
  series present for metrics/logs/traces/grafana (note: Grafana 12 no longer exposes
  `process_resident_memory_bytes` — use its `go_*`/`grafana_*` metrics in the self-monitoring
  dashboard); OTLP smoke span POSTed to `http://bhgrafana.flycast:10428/insert/opentelemetry/v1/traces`
  returned 200 and was read back via both `/select/tempo/api/traces/<id>` and the Jaeger API
  (allow a few seconds of ingestion-visibility lag before querying). Only IP on the app:
  private flycast. Access until the Netbird peer exists: `fly proxy 3000 -a bhgrafana` →
  http://localhost:3000 (proxies to the machine's Grafana port directly; note `fly proxy`
  cannot resolve `.flycast` names from outside the network, and the machine itself has
  nothing on port 80 — that port only exists on the flycast proxy address).

## 2026-08-28 — Add VictoriaTraces (traces phase 1)

### Version pins
- **VictoriaTraces v0.10.0** (`victoriametrics/victoria-traces:v0.10.0`). Latest at pin time
  was v0.11.0 (2026-08-14) but it is flagged pre-release upstream; v0.10.0 (2026-07-22) is the
  release marked *Latest*. VictoriaTraces is **pre-GA**: read the changelog before every bump,
  and treat trace data as disposable. The Tempo query endpoint requires ≥ v0.9.4.
- VictoriaMetrics v1.118.0 and VictoriaLogs v1.22.2 pins inherited from upstream, unchanged.

### Topology: single machine, single process group
The original plan split the app into `collect` (Vector + Victoria stores) and `grafana`
process groups. Decided against it and kept the template's single-machine layout: if any
collector is down, Grafana is degraded anyway, and one machine is less to operate. Everything
talks over `localhost`, so the Grafana datasources stay file-provisioned and unchanged apart
from the new traces entry. Consequence kept in mind: one volume's 64 MiB/s bandwidth cap is
shared by metrics+logs+traces, and one `fly deploy` rolls everything together. The vm size in
`fly.toml` is `shared-cpu-8x` / 2GB (CPU-biased pick; app name `bhgrafana`). 2GB plus 1GB swap
is shared by all five processes, so memory is the first thing to watch on the self-monitoring
scrape below — that's the tripwire for resizing or splitting later.

### Build change (important)
`fly.toml` no longer uses `build.image = "flyio/fly-telemetry"` — the upstream prebuilt image
doesn't contain VictoriaTraces. Deploys now build the local `Dockerfile`, which means the
`dashboards/` **git submodule must be checked out** at deploy time
(`git clone --recurse-submodules` or `git submodule update --init`).

### Trace ingest & query wiring
- `start.sh` launches `/victoria-traces-prod` with `-storageDataPath /data/traces`
  (matches the `/data/metrics`, `/data/logs` convention — the plan's `/data/victoria-traces`
  path was adjusted to fit), `-retentionPeriod 14d` (default is 7d; 14d is enough to debug
  across a sprint boundary while keeping the disposable-data stance), `-httpListenAddr :10428`.
  `-envflag.enable` + `enableTCP6` makes it listen on IPv6, required for Fly private networking.
- OTLP HTTP ingest exposed via Flycast: raw TCP service on port 10428 in `fly.toml`.
  Services set `OTEL_EXPORTER_OTLP_ENDPOINT=http://<app>.flycast:10428/insert/opentelemetry`
  (base path; SDKs append `/v1/traces`) with `http/protobuf`.
- Grafana queries traces through the core **Tempo** datasource type pointed at
  `http://localhost:10428/select/tempo` (uid `victoria_traces`, for later trace↔logs links).
  Keeping the Tempo-compatible endpoint makes a future swap to actual Tempo a datasource-URL
  change, which is the cheap exit if pre-GA instability bites.

### No OTel collector (deliberate)
Apps export OTLP straight to VictoriaTraces. A collector adds a hop and an ops surface with no
current payoff; it gets reconsidered only if we need sampling, fan-out to a second backend, or
dual-write HA.

### Non-HA (deliberate)
Single instance, no replication. `fly-metrics.net` and `fly logs` remain the platform fallback
during outages, plus daily volume snapshots on the data volume. Trace data is explicitly
disposable at 14d retention.

### Self-monitoring
Vector now also scrapes `localhost:10428/metrics` (VictoriaTraces, tagged `process=traces`) and
`localhost:3000/metrics` (Grafana, tagged `process=grafana`) alongside VM/VLogs. That feeds the
planned self-monitoring dashboard (`process_resident_memory_bytes` per component) — the trigger
for future "resize memory" / "split query load" decisions. If the shared volume ever fills,
`-retention.maxDiskSpaceUsageBytes` on victoria-traces is the lever to cap trace disk usage.

### Security posture (unchanged)
Grafana stays anonymous-admin; the auth boundary is network-level (Flycast only, no public
IPs — verify with `fly ips list` after every launch/config change). Grafana auth moves out of
the deferred list if mesh access ever widens beyond the eng team.
