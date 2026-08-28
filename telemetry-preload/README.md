# @beehive/telemetry-preload

Zero-app-code telemetry for BetStudio Node services. One preload, two jobs:

1. **Distributed tracing** — OpenTelemetry auto-instrumentation: `traceparent`
   propagation on 100% of requests, spans exported to VictoriaTraces at the
   sampled ratio (default **10%**), and `trace_id` injected into every winston
   log record. Devs keep writing ordinary `logger.info(...)` calls.
2. **HTTP request/response logging** — the 100% record of *which request went
   where and responded how*: an `http.access` line for every request, and an
   `http.payload` line (redacted headers + capped bodies) on errors/slow
   requests by default. JSON on stdout → Fly log stream → VictoriaLogs.

Both halves fail open: any init error logs once and the app boots normally.

## Wiring a service (no app code)

Vendor this directory into the image and preload it:

```dockerfile
# in the service's Dockerfile (or the shared base image)
COPY telemetry-preload /opt/telemetry-preload
RUN cd /opt/telemetry-preload && npm install --omit=dev
ENV NODE_OPTIONS="--require /opt/telemetry-preload"
```

Then set per-app env (flydeck / `fly secrets set` / `[env]`):

```
OTEL_EXPORTER_OTLP_ENDPOINT=http://bhgrafana.flycast:10428/insert/opentelemetry
```

That endpoint is the tracing master switch — unset means tracing is off
(local dev, CI). `OTEL_SERVICE_NAME` defaults to `FLY_APP_NAME`. The HTTP
logger is on by default (`HTTP_LOG=off` to disable).

**Node version:** works on Node 20 (server-wrap mode, the same mechanism every
APM agent uses). On **Node ≥ 22** it automatically switches to the built-in
`diagnostics_channel` API — no patching at all. Standardising on Node 24 LTS
is the recommended target.

## Env reference

| Var | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset = tracing off)* | Base OTLP endpoint; SDK appends `/v1/traces` |
| `OTEL_TRACES_SAMPLER_ARG` | `0.1` | Trace sample ratio (parent-based: whole requests, all hops) |
| `OTEL_SERVICE_NAME` | `$FLY_APP_NAME` | Service name on spans/logs |
| `OTEL_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Never traced (exact, or `prefix/`) |
| `OTEL_SDK_DISABLED` | | `true` kills tracing regardless of endpoint |
| `HTTP_LOG` | `on` | `off` disables the HTTP logger |
| `HTTP_LOG_PAYLOAD` | `errors` | `errors` (4xx/5xx or slow) \| `always` \| `off` |
| `HTTP_LOG_SLOW_MS` | `1000` | "errors" tier also fires above this duration |
| `HTTP_LOG_BODY_MAX` | `4096` | Bytes kept per body (request and response each) |
| `HTTP_LOG_PAYLOAD_ROUTES` | | Comma path-prefixes that always log payloads (dispute-prone routes) |
| `HTTP_LOG_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Requests skipped entirely |
| `HTTP_LOG_MODE` | `auto` | `channels` (Node ≥22) \| `wrap` \| `auto` |

## What lands in VictoriaLogs

```json
{"level":"info","message":"http.access","logger":"http","service":"pgs-api","method":"POST","path":"/api/bets","status":200,"duration_ms":42.3,"trace_id":"4bf92f35...","span_id":"00f067aa..."}
{"level":"debug","message":"http.payload","logger":"http","service":"pgs-api","path":"/api/bets","status":500,"req_headers":{...},"req_body":"{\"stake\":100,\"token\":\"[REDACTED]\"}","res_body":"{\"error\":...}","trace_id":"4bf92f35..."}
```

Query one request across every service: `trace_id:4bf92f35...` — PGS's,
bo-api's and core's lines come back together, payloads included where policy
allowed. The same id opens the span waterfall in the VictoriaTraces datasource
for the sampled 10%.

## Redaction (`redact.js`)

Ported from softstudio-bo's proven `redact.util.ts`: normalised key matching
(`x-api-key` ≡ `apiKey`), whole-word credentials (`pin`, `pan`, `iban`),
header **allowlist** (fails closed), deep body redaction, query-string
redaction. Spans get the same policy structurally via
`ScrubbingSpanProcessor` — a dependency bump that re-enables header capture
upstream degrades into scrubbed attributes, not leaked tokens.

## Migrating a service that already has in-repo OTel

Running this preload **and** an in-repo SDK double-instruments the process.
At cutover:

- `softstudio-bo/apps/api`: delete `src/tracing.ts` and the `--require
  ./dist/tracing.js` from Dockerfile CMD + `start:prod`. Sentry stays
  app-owned (its init moves back to a plain import if still wanted).
- `softstudio-core-v2/apps/core`: delete `src/common/utils/otel-instrument.ts`
  (keep `sentry-instrument.ts` minus the otel import) and the
  `GRAFANA_OTLP_*` secrets.

## Known limits

- Response bodies of streaming/SSE endpoints are capped like everything else;
  websocket upgrades are skipped entirely.
- If no other consumer reads a request body, the tap drains it (harmless).
- The .NET sports service needs its own middleware for payload logging; CLR
  zero-code auto-instrumentation covers its traces.
- Payload logging at `always` + anonymous Grafana = everyone on the mesh can
  read bodies. Flipping payloads to `always` fleet-wide is the trigger to add
  Grafana auth (see the deferred table in the plan).
