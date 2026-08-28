# Devlog

Decision record for this fork. Newest entries first.

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
