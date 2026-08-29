# bhcs — ClickStack trial

Separate Fly app trialing [ClickStack](https://clickhouse.com/docs/use-cases/observability/clickstack/overview)
(ClickHouse + HyperDX UI + OTel collector + MongoDB) for **application-side observability**:
exceptions with session replay, browser network capture, OTLP traces/logs, one SDK for devs.

**bhgrafana is untouched.** It keeps doing platform telemetry (NATS logs/metrics, PromQL
dashboards, infra alerting). This trial is isolated: rollback = `fly apps destroy bhcs`.

## Phases

1. **Now — SDK-only.** Org services and (mesh-connected) browsers send OTLP straight here.
   No NATS, no Vector changes. This exercises exactly the features being evaluated.
2. **If convinced — platform-log dual-write.** ~10 lines added to bhgrafana's `vector.yaml`
   (snippet at the bottom) put Fly platform logs beside app telemetry in HyperDX.
3. **Maybe/later — metrics + lazy migration.** Platform metrics dual-write and dashboard-by-
   dashboard retirement of Grafana, ideally after PromQL lands in ClickHouse. See DEVLOG.

## Deploy (phase 1)

```shell
# From the repo, cd into this directory first — flyctl reads ./fly.toml + ./Dockerfile.
cd bhcs

# 1. App on the ORG NETWORK (mandatory — see DEVLOG 2026-08-28: networks are fixed
#    at creation and mutually isolated).
fly apps create bhcs --org beehive-gaming --network production

# 2. Volume (daily snapshots are on by default; start modest, `fly vol extend` grows online).
fly volumes create data --region sin --size 50 -a bhcs

# 3. Deploy privately.
fly deploy --flycast

# 4. THE IP CHECK (DEVLOG 2026-08-28 quirk): `--flycast` may allocate on the DEFAULT
#    network. Verify the flycast prefix matches the machines' 6PN prefix; if not:
fly ips list -a bhcs
# fly ips release <wrong-ip> -a bhcs
# fly ips allocate-v6 --private --network production -a bhcs
```

## First login & API key

HyperDX has real auth (unlike anonymous Grafana). From a mesh peer open
`http://bhcs.flycast/`, or without mesh: `fly proxy 8080:8080 -a bhcs` →
http://localhost:8080. Create the team account, then grab the **ingestion API key**
from Team Settings — SDKs and OTLP exporters must send it.

## Point a service at it

```shell
OTEL_SERVICE_NAME=$FLY_APP_NAME
OTEL_EXPORTER_OTLP_ENDPOINT=http://bhcs.flycast:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=authorization=<INGESTION_API_KEY>
OTEL_RESOURCE_ATTRIBUTES=fly.region=$FLY_REGION,deployment.environment=prod
```

Node services can use `@hyperdx/node-opentelemetry` instead (adds console/pino/winston
capture + auto HTTP instrumentation). Browser testing from mesh-connected dev machines:
`@hyperdx/browser` with `url: 'http://bhcs.flycast:4318'` + the API key — session replay
works privately for internal testers. **Real players need a public ingest endpoint —
that is a deliberate, separate step** (public IP exposes UI+API too; auth boundary
becomes HyperDX login + API key; set replay masking BEFORE any real traffic:
`maskAllInputs: true`, `maskAllText: true`, `blockSelector` on sensitive regions,
leave `advancedNetworkCapture` off for player-facing apps).

## Verification checklist (run all of it, devlog the results)

```shell
# a. Processes came up clean?
fly logs -a bhcs        # look for permission errors on /data/clickhouse (fix below)

# b. ClickHouse overrides actually applied? (If these return defaults, the image's
#    config dir differs from /etc/clickhouse-server — see Troubleshooting.)
fly ssh console -a bhcs -C "clickhouse-client --query \"SELECT name, value FROM system.settings WHERE name IN ('max_memory_usage','max_bytes_before_external_group_by','join_algorithm','max_threads','max_execution_time')\""
fly ssh console -a bhcs -C "clickhouse-client --query \"SELECT name, value FROM system.server_settings WHERE name IN ('max_server_memory_usage','mark_cache_size')\""
fly ssh console -a bhcs -C "ls /data/clickhouse /data/db"

# c. OTLP smoke test (JSON over OTLP/HTTP) from any machine on the network:
curl -s -X POST http://bhcs.flycast:4318/v1/logs \
  -H 'Content-Type: application/json' -H 'authorization: <INGESTION_API_KEY>' \
  -d '{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"smoke"}}]},"scopeLogs":[{"logRecords":[{"body":{"stringValue":"bhcs smoke test"}}]}]}]}'
# then search "bhcs smoke test" in the HyperDX UI.

# d. THE PERSISTENCE TRIPWIRE (do not skip): with the smoke event ingested —
fly machine restart -a bhcs      # still searchable after restart?
fly deploy --flycast             # still searchable after a redeploy?
# If either loses data, a database is writing to overlay instead of /data. Stop and fix.

# e. No public IPs (phase-1 doctrine):
fly ips list -a bhcs
```

## Troubleshooting

- **Permission denied on `/data/clickhouse*`**: the volume mount is root-owned. One-time:
  `fly ssh console -a bhcs -C "chown -R clickhouse:clickhouse /data/clickhouse /data/clickhouse-tmp"`, then restart.
- **UI loads, API calls fail**: the browser must reach port 8000 (exposed in fly.toml).
  If paths/urls are wrong, check the image's frontend/api URL env vars in the ClickStack
  all-in-one docs for the pinned version.
- **CH overrides ignored**: confirm the image keeps config at `/etc/clickhouse-server/`
  (`fly ssh console -a bhcs -C "ls /etc/clickhouse-server/config.d"`).
- **Memory watch**: `fly ssh console -a bhcs -C "clickhouse-client --query 'SELECT metric, value FROM system.asynchronous_metrics WHERE metric LIKE \'%MemoryResident%\''"` + machine metrics in bhgrafana.

## Phase 2 snippet (do NOT apply during the trial)

Dual-write platform logs from bhgrafana → bhcs. Prereqs: ClickHouse must listen beyond
localhost (`<listen_host>::</listen_host>` in config.d) **and** get a password-protected
ingest user first — flycast/6PN is org-wide, don't expose a passwordless ClickHouse.

```yaml
# add to vector.yaml sinks: (plus a fly_platform_logs table + HyperDX custom source)
  clickstack_logs:
    inputs: [logs]
    type: clickhouse
    endpoint: http://bhcs.internal:8123
    auth: {strategy: basic, user: vector, password: "${BHCS_CH_PASSWORD?}"}
    database: default
    table: fly_platform_logs
    compression: gzip
    batch: {max_bytes: 10000000, timeout_secs: 5}
```
