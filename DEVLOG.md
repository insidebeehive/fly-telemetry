# Devlog

Decision record for this fork. Newest entries first.

## 2026-08-30 — ClickStack trial (`bhcs/`, separate app)

- **Trialing ClickStack for app-side observability** (exceptions + session replay, HTTP
  request/response logging, one SDK for devs) as a **separate app `bhcs`** — this stack is
  untouched and keeps VictoriaMetrics/VictoriaLogs ingestion and the PromQL dashboards.
  Rollback = `fly apps destroy bhcs`.
- **No fork.** `bhcs/Dockerfile` is packaging-only over the pinned upstream image. Images
  migrated from `docker.hyperdx.io/hyperdx/*` to Docker Hub `clickhouse/clickstack-*`;
  pinned `clickhouse/clickstack-all-in-one:2.37.0` (newest release tag at pin time).
  ClickHouse releases monthly — read the changelog before every bump.
- **Ingestion split (no SDKs are deployed yet, so nothing depends on one):**
  platform **metrics** can only come from NATS — no SDK emits `fly_*` series, they describe
  machines and proxies. **Logs** come from NATS today and are **env-toggleable**
  (`INGEST_NATS_LOGS`, `INGEST_NATS_METRICS`) so they can move to the SDKs later by flipping
  a variable and restarting, no rebuild. **Traces/exceptions/replay** are SDK-only.
  `vector` (pinned 0.46.1 for the same NATS-auth reason as here) runs inside the image;
  queue group `bhcs` means it gets its OWN copy of the streams and does not steal messages
  from bhgrafana's group.
- **Sampling design: 10% of spans, 100% of HTTP request/response.** Trace sampling only
  drops spans; log records are never sampled. So HTTP detail is emitted as *log records*
  (pino/winston with an explicit `redact` list), not span attributes, and survives at 100%
  while `OTEL_TRACES_SAMPLER_ARG=0.1` cuts span volume. Known consequence: ~90% of HTTP logs
  carry a TraceId whose trace was not stored, so "view trace" dead-ends — acceptable because
  the log itself carries the full detail. Browser `advancedNetworkCapture` stays OFF for
  player-facing apps: it captures full headers/bodies with no documented redaction hook.
- **Findings from unpacking the image** (registry API; no docker available here):
  entrypoint is `sh /etc/local/entry.sh`, which starts ClickHouse, `mongod`, the collector
  and the HyperDX app and then `wait -n` — **the container exits if any of them dies**, so
  Fly restarts the machine instead of running half-alive (better than the failure mode this
  stack hit on 2026-08-28). Vector is not in that wait set, so `bhcs/vector.sh` keeps its own
  retry loop. API port is 8000, UI 8080, collector health 13133 (wired as a Fly check).
- **Collector receivers/exporters are injected at runtime over OpAMP**, and config merging
  replaces lists rather than appending — so the NATS-metrics scrape lives in its own
  `metrics/fly` pipeline with its own receiver and exporter, where the dynamic config cannot
  clobber it. `CUSTOM_OTELCOL_CONFIG_FILE` is honored in both supervisor and standalone modes
  (verified in the image's `/otel-entrypoint.sh`). Seed DDL matches the contrib exporter's
  standard `otel_metrics_*` layout, so `create_schema: false` writes to HyperDX's own tables.
  Platform logs go straight to `otel_logs` (schema read from the seed migration), which
  bypasses the collector's `transform` processor — hence severity mapping in VRL.
- **Single 8GB machine, single volume** (the bhgrafana posture; ClickStack's compose docs
  bless single-server/no-fault-tolerance production). Mongo already writes `/data/db`;
  ClickHouse moved beside it via a `config.d` `<path>` override with `tmp/` and `access/`
  inside it so the stock entrypoint's recursive chown covers them. The image default
  `/var/lib/clickhouse` is ephemeral overlay — this override IS the fix for the documented
  all-in-one persistence caveat. `kill_timeout=120s` for clean ClickHouse shutdowns.
- **Memory governance so querying can never kill ingestion** (defaults are mutually
  suicidal: CH assumes ~90% of RAM, WiredTiger 50%): server capped at 4GB absolute,
  per-query 3GB with spill-to-disk at 1.5GB (external GROUP BY/sort on the volume,
  `grace_hash` joins), `max_threads=4`, `max_execution_time=300`, Node capped via
  `NODE_OPTIONS`. Mongo cache left default (metadata is tiny, cache grows lazily) — on the
  watch list. Failure mode by design: oversized queries run slow or error; never an OOM.
- **Private-first (flycast only)**: UI 8080 + API 8000 + OTLP 4317/4318, no public IPs in
  phase 1. Ports are mapped 1:1 rather than via `[http_service]` on 80 because HyperDX builds
  its own URLs from the app/API ports. Real-player RUM later needs a public IP — a deliberate
  doctrine change (auth boundary becomes HyperDX login + ingestion API key), with replay
  masking (`maskAllInputs`/`maskAllText`) mandatory first: HyperDX defaults are permissive,
  the opposite of Sentry's.
- **Untested until deployed** (no Fly/docker access from the authoring session): the
  OpAMP/custom-config merge, the `otel_logs` VRL mapping, and flycast-direct UI→API. Runbook
  in `bhcs/README.md` verifies each with a query, plus the **persistence tripwire**
  (restart + redeploy) and a no-public-IP check.

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
