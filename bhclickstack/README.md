# bhclickstack — ClickStack trial

Separate Fly app trialing [ClickStack](https://clickhouse.com/docs/use-cases/observability/clickstack/overview)
(ClickHouse + HyperDX UI + OTel collector + MongoDB) as the single investigation UI for
application-side observability: exceptions with session replay, HTTP request/response
logging, traces, and platform telemetry in one place.

**bhgrafana is untouched.** It keeps ingesting the same NATS streams into VictoriaMetrics /
VictoriaLogs and serving the PromQL dashboards. Rollback is `fly apps destroy bhclickstack`.

## How data gets in

| Signal | Source | Why |
| --- | --- | --- |
| Platform **metrics** (`fly_*`) | NATS → vector → collector | No SDK can emit these — Fly produces them *about* machines/proxies |
| **Logs** | NATS → vector → ClickHouse (toggleable) | Works with zero SDKs installed today; later, SDKs can take over |
| **Traces**, exceptions, replay | SDK → OTLP | Only the SDK can see inside the app |

Two env toggles in `fly.toml`, flipped with `fly secrets`/`fly deploy` and a restart — no rebuild:

- `INGEST_NATS_LOGS` (default `true`) — platform logs into `otel_logs`, the same table
  HyperDX's own "Logs" source reads. Set `false` once SDK logs are flowing, or leave it on:
  it also covers apps that will never be instrumented plus Fly-side lines (proxy errors, OOM
  kills, machine start/stop) that no SDK emits.
- `INGEST_NATS_METRICS` (default `true`) — the only path for `fly_*` series.

`vector` runs inside the image next to the stock ClickStack processes, subscribing with
queue group `bhclickstack`. **Queue groups are per-app, so this app receives its own complete copy
of the streams and does not steal messages from bhgrafana.**

## Deploy

```shell
cd bhclickstack   # flyctl reads ./fly.toml + ./Dockerfile

# 1. App on the ORG NETWORK (mandatory — ../DEVLOG.md 2026-08-28: networks are fixed at
#    creation and mutually isolated).
fly apps create bhclickstack --org beehive-gaming --network production

# 2. Volume (daily snapshots on by default; `fly vol extend` grows it online).
fly volumes create data --region sin --size 50 -a bhclickstack

# 3. Same readonly org token bhgrafana uses for the platform streams.
fly secrets set ACCESS_TOKEN="$(fly tokens create readonly beehive-gaming)" --stage -a bhclickstack

# 4. Deploy privately.
fly deploy --flycast

# 5. THE IP CHECK (../DEVLOG.md 2026-08-28 quirk): --flycast may allocate on the DEFAULT
#    network. The flycast prefix must match the machines' 6PN prefix.
fly ips list -a bhclickstack
# fly ips release <wrong-ip> -a bhclickstack
# fly ips allocate-v6 --private --network production -a bhclickstack
```

## First login — REQUIRED before OTLP ingest works

Until an account exists, `teams` is empty, HyperDX's OpAMP controller has no config to hand
out, and **the collector will not start at all** (port 13133 dead, no 4317/4318, health check
critical). Creating the account is therefore a deploy step, not a nicety. It has to be done
by a human — it is the owner's own credential. After registering, restart the machine
(`fly machine restart -a bhclickstack`) so the collector picks up its config.

The NATS→ClickHouse log bridge is unaffected by this: it writes ClickHouse directly and
ingests fine with the collector down.

HyperDX has real auth (unlike anonymous Grafana). Most reliable access path:

```shell
fly proxy 8080:8080 8000:8000 -a bhclickstack   # UI and API on matching localhost ports
```

then http://localhost:8080 — create the team account and copy the **ingestion API key**
from Team Settings. From a mesh peer, `http://bhclickstack.flycast:8080` should also work
(`HYPERDX_APP_URL` is set for that); if the UI loads but API calls fail, fall back to
`fly proxy` and see Troubleshooting.

## Instrumenting a service: 10% spans, 100% HTTP logs

The rule that makes this work: **trace sampling only drops spans. Log records are never
sampled.** So HTTP request/response detail must be emitted as *log records*, not as span
attributes — then it survives at 100% while spans stay at 10%.

```shell
OTEL_SERVICE_NAME=$FLY_APP_NAME
OTEL_EXPORTER_OTLP_ENDPOINT=http://bhclickstack.flycast:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=authorization=<INGESTION_API_KEY>
OTEL_RESOURCE_ATTRIBUTES=fly.region=$FLY_REGION,deployment.environment=prod

# 10% of traces, head-sampled in-process (the 90% never leave the app).
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=0.1
```

For the HTTP log itself, use a request logger with explicit redaction — Node's
`@hyperdx/node-opentelemetry` already ships pino/winston transports that attach trace
context automatically:

```js
const pinoHttp = require('pino-http')({
  redact: {
    paths: [
      'req.headers.authorization', 'req.headers.cookie', 'res.headers["set-cookie"]',
      'req.body.password', 'req.body.pin', 'req.body.cvv', 'req.body.cardNumber',
      'req.body.token', 'res.body.token',
    ],
    censor: '[redacted]',
  },
})
```

Notes worth knowing before rollout:

- **Redact before you capture.** The browser SDK's `advancedNetworkCapture` grabs full
  headers and bodies with *no documented redaction hook* — leave it off for player-facing
  apps and do request/response logging server-side, where the `redact` list above applies.
  Same for replay: HyperDX defaults are permissive, so set `maskAllInputs: true`,
  `maskAllText: true` and `blockSelector` on sensitive regions before any real traffic.
- **Orphaned trace links are expected at 10%.** An HTTP log records its `TraceId` even when
  that trace wasn't sampled, so "view trace" dead-ends ~90% of the time. The log carries the
  full request/response detail, which is the point of keeping 100% of them.
- The collector's `transform` processor parses JSON log bodies and promotes
  `level`/`severity` fields automatically, so structured pino/winston output arrives with
  correct severity with no extra config.
- Browsers can only reach OTLP over the mesh while the app is private. **Real-player RUM
  needs a public ingest endpoint — a deliberate, separate step** (a public IP exposes the UI
  and API too; the auth boundary becomes HyperDX login + the ingestion API key).

## Verification checklist (run all of it; devlog the results)

```shell
# a. Clean startup? Look for vector's "bridge ENABLED" lines and any ClickHouse
#    permission errors on /data/clickhouse.
fly logs -a bhclickstack

# b. ClickHouse overrides actually applied? (If these return defaults, the image's config
#    dir differs from /etc/clickhouse-server — see Troubleshooting.)
fly ssh console -a bhclickstack -C "clickhouse-client --query \"SELECT name, value FROM system.settings WHERE name IN ('max_memory_usage','max_bytes_before_external_group_by','join_algorithm','max_threads')\""
fly ssh console -a bhclickstack -C "clickhouse-client --query \"SELECT name, value FROM system.server_settings WHERE name IN ('max_server_memory_usage','mark_cache_size')\""
fly ssh console -a bhclickstack -C "ls /data/clickhouse /data/db"

# c. NATS logs bridge landing rows in the table HyperDX reads?
fly ssh console -a bhclickstack -C "clickhouse-client --query \"SELECT ServiceName, count() FROM default.otel_logs WHERE Timestamp > now() - 300 GROUP BY ServiceName ORDER BY count() DESC LIMIT 10\""

# d. NATS metrics bridge — vector exporting, and the collector pipeline landing fly_* series?
fly ssh console -a bhclickstack -C "curl -s http://127.0.0.1:9598/metrics | head -5"
fly ssh console -a bhclickstack -C "clickhouse-client --query \"SELECT count(), uniq(MetricName) FROM default.otel_metrics_gauge WHERE MetricName LIKE 'fly_%' AND TimeUnix > now() - 600\""

# e. OTLP smoke test from any machine on the network:
curl -s -X POST http://bhclickstack.flycast:4318/v1/logs \
  -H 'Content-Type: application/json' -H 'authorization: <INGESTION_API_KEY>' \
  -d '{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"smoke"}}]},"scopeLogs":[{"logRecords":[{"body":{"stringValue":"bhclickstack smoke test"}}]}]}]}'
# then search "bhclickstack smoke test" in the UI.

# f. THE PERSISTENCE TRIPWIRE (do not skip):
fly machine restart -a bhclickstack      # smoke event still searchable?
fly deploy --flycast             # still searchable after a redeploy?
# If either loses data, a database is writing to overlay instead of /data. Stop and fix.

# g. No public IPs (phase-1 doctrine):
fly ips list -a bhclickstack
```

## Troubleshooting

- **`fly_*` series missing but `curl 127.0.0.1:9598/metrics` works** → the custom collector
  pipeline didn't merge. Check `fly logs` for `Including custom config` /
  `Using custom OTEL config file`, and for a `clickhouse/fly` exporter error. If the
  exporter name is rejected by this build, the fallback is to point vector's
  `prometheus_exporter` at a `prometheusremotewrite` receiver instead, or (last resort)
  write a custom table and register it as a HyperDX metric source.
- **`otel_logs` inserts failing** → schema drift between the seed migration and the vector
  mapping. `fly ssh console -a bhclickstack -C "clickhouse-client --query 'DESCRIBE default.otel_logs'"`
  and reconcile `vector-templates/nats-logs.yaml`.
- **Permission denied on `/data/clickhouse`** → the stock entrypoint chowns the configured
  data path, but if the volume mount races it:
  `fly ssh console -a bhclickstack -C "chown -R clickhouse:clickhouse /data/clickhouse"`, then restart.
- **UI loads, API calls fail from flycast** → the image derives its API URL from
  `HYPERDX_API_PORT` and defaults to `127.0.0.1`. Use `fly proxy 8080:8080 8000:8000` (both
  ports on localhost) or experiment with `HYPERDX_API_URL` / `SERVER_URL`.
- **Memory watch** — the tripwire for resizing:
  `fly ssh console -a bhclickstack -C "clickhouse-client --query \"SELECT metric, formatReadableSize(value) FROM system.asynchronous_metrics WHERE metric LIKE '%MemoryTracking%'\""`.
  MongoDB's WiredTiger cache is left at its default (50% of RAM−1GB) because HyperDX's
  Mongo holds only dashboards/users/alerts and the cache grows lazily; if it ever shows up
  in machine memory, patch `mongod` startup with `--wiredTigerCacheSizeGB 0.25`.

## Retention

The seed migrations apply TTLs from `LOGS_TTL` / `METRICS_TTL` / `TRACES_TTL` at first
migrate. Set them before the first deploy if the defaults don't match the disk budget —
write-heavy plus rarely-read means disk is the real constraint here, not RAM.
