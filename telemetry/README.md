# @insidebeehive/telemetry

Zero-code telemetry for BetStudio Node services on Fly.io. One dependency,
one env var, no app code:

- **Traces** — OpenTelemetry auto-instrumentation (http, express, nest, pg,
  ioredis, mongodb), parent-based sampling at 10%, exported over OTLP to the
  endpoint each app sets (`OTEL_EXPORTER_OTLP_ENDPOINT` — deliberately no
  baked default; unset means tracing stays off). Winston/pino log records get
  `trace_id` injected automatically — the log↔trace join key.
- **HTTP logging** — exactly one `http.access` JSON line per request (100%:
  method / path / route / url / host / status / duration / ip / trace_id);
  when the payload policy fires, the **same line** also carries redacted
  headers and capped JSON bodies, so a failed transaction's record is
  self-contained. Lines carry `logger=http`, the VictoriaLogs stream field
  the Grafana dashboards query, and reach VictoriaLogs via stdout → Fly
  logs → Vector. No winston coupling.

- **App logger** — `import { logger } from "@insidebeehive/telemetry"` and
  `logger.info(...)` anywhere: preconfigured winston, JSON on Fly/production,
  pretty locally, `logger=app` stream field, `service` stamped, `trace_id`
  auto-injected inside requests. No per-app logging setup at all.

Everything is fail-safe (a telemetry bug never stops an app booting) and
stays silent in local dev, tests and CI: tracing is off until an endpoint is
configured, and http logging auto-enables only on Fly (`FLY_APP_NAME`
present).

## Integration (per app)

1. Install (public on npm — no registry config needed):

   ```sh
   npm install @insidebeehive/telemetry
   ```

2. Activate in `fly.toml` — the same line for NestJS (CJS) and Remix (ESM):

   ```toml
   [env]
     NODE_OPTIONS = "--import @insidebeehive/telemetry/register"
     OTEL_EXPORTER_OTLP_ENDPOINT = "http://<collector-app>.flycast:10428/insert/opentelemetry"
     # optional — defaults to FLY_APP_NAME, then the app's package.json name:
     # OTEL_SERVICE_NAME = "core-stage"
   ```

   The endpoint is the base path — the SDK appends `/v1/traces` itself.

3. Keep the Dockerfile CMD a direct `node <entry>` (`node dist/main.js`,
   `node ./build/server/index.js`) — never `npm start`, which would boot the
   SDK inside npm's own node process too.

That's it. Verify after deploy: boot logs show
`[telemetry] tracing enabled -> …` and
`[telemetry] http logger enabled …`; Grafana → Explore →
VictoriaLogs → `_stream:{logger="http", fly.app.name="<app>"}` shows access
lines; the traces datasource shows spans for the service.

**Prefer explicit code over NODE_OPTIONS?** Same package, first line of the
entrypoint: `require("@insidebeehive/telemetry").init()`. Both styles are
guarded — double activation is a no-op. **Migrating a service that already
has an in-repo OTel bootstrap (tracing.ts):** delete it when enabling this
package, or the process is double-instrumented.

**Turning this on replaces per-app access logging** — disable morgan /
NestJS logging interceptors that print per-request lines, or the
`logger=http` stream gets doubled entries.

## App logger

No per-app winston setup — import and log, from any file:

```js
import { logger } from "@insidebeehive/telemetry";

logger.info("bet placed", { betId, amount });
const walletLog = logger.child({ module: "wallet" });   // plain winston child

try {
  await capturePayment(order);
} catch (err) {
  // custom message + full stack + context fields, all in one line:
  logger.error("payment capture failed", { err, orderId: order.id });
  // bare form works too: logger.error(err)
}
```

On Fly / in production each call is one JSON line on stdout:

```json
{"level":"info","message":"bet placed","timestamp":"2026-09-01T10:24:03.512Z","logger":"app","service":"core-stage","betId":"b_991","amount":250,"trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7"}
```

Locally it pretty-prints with colors instead. Notes:

- `logger=app` is stamped via winston `defaultMeta` — the stream-level
  partner of the http logger's `logger=http`. App logs and the HTTP firehose
  are separate VictoriaLogs streams; Grafana queries either without scanning
  the other.
- `trace_id`/`span_id` appear automatically on lines logged inside a request
  (OTel winston instrumentation) — the pivot between an app log, its
  `http.access` line and its spans.
- `LOG_LEVEL` env sets the level (default `info`). It gates **only this app
  logger** — the http logger writes to stdout directly and always emits its
  line regardless of any log level.
- Errors anywhere in the meta are serialised with message + stack
  (`{ err }` above); winston alone would log them as `{}`.
- **Uncaught exceptions and unhandled rejections** are captured as one
  structured JSON line (same format, `logger=app`, `service`, stack), then
  the process exits and Fly restarts it. Without this, a crash is a raw
  multi-line stack on stderr that the line-based log stream splits into
  unqueryable fragments. Handlers are registered at activation (`init()`),
  so even a boot crash is covered.
- NestJS apps can route Nest's own logging through it:
  `WinstonModule.createLogger({ instance: logger })` (nest-winston).
- Existing app loggers keep working and still get trace injection; they just
  don't carry `logger=app` unless you add it to their defaultMeta. The
  export is the zero-config path. It's built lazily, so pino-only apps never
  load winston.

## What the HTTP log line looks like

**Exactly one line per request**, message type `http.access`. A normal
request under the default policy:

```json
{"level":"info","message":"http.access","ts":"2026-09-01T10:24:03.512Z","logger":"http","service":"core-stage","method":"POST","path":"/api/v1/bets","route":"/api/v1/bets","url":"/api/v1/bets?gameId=g_123&token=[REDACTED]","host":"core-stage.fly.dev","status":201,"duration_ms":42.7,"ip":"203.0.113.7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7","res_bytes":86}
```

When the `HTTP_LOG_PAYLOAD` policy fires (default `errors`: 4xx/5xx and slow
requests; `always` attaches on every request), the **same line** carries the
evidence too:

```json
{"level":"info","message":"http.access","ts":"2026-09-01T10:24:03.512Z","logger":"http","service":"core-stage","method":"POST","path":"/api/v1/bets","route":"/api/v1/bets","url":"/api/v1/bets?gameId=g_123&token=[REDACTED]","host":"core-stage.fly.dev","status":422,"duration_ms":42.7,"ip":"203.0.113.7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7","res_bytes":86,"req_headers":{"host":"core-stage.fly.dev","content-type":"application/json","content-length":"64","user-agent":"Mozilla/5.0 …","origin":"https://app.example.com","x-forwarded-for":"203.0.113.7","userid":"u_4821","traceparent":"00-4bf92f…-…-01"},"req_body":{"amount":250,"gameId":"g_123","token":"[REDACTED]"},"res_headers":{"content-type":"application/json","content-length":"86"},"res_body":{"error":"insufficient_balance","balance":120}}
```

All lines share one shape and one message type: `_stream:{logger="http"}`
selects everything, `status:>=500` the failures, `req_body:*` the enriched
lines.

**Body shape** — parsed JSON bodies are logged as **objects** (redacted
first), so VictoriaLogs indexes their keys as fields at ingest and business
filters are direct — no unpack pipes:

```
_stream:{logger="http", fly.app.name="core-stage"} req_body.amount:>100
```

This is safe because VictoriaLogs flattens nested dicts with dots
(`req_body.bet.amount`) and **converts arrays to strings** at ingest — a
200-item array can never mint 200 field names. Bodies that cannot be
objects stay strings in any mode: truncated ones (over `HTTP_LOG_BODY_MAX`),
non-JSON text, and `"[gzip N bytes]"`-style placeholders — field queries
like `req_body.amount:…` simply skip those rows.

Per-app escape hatch: `HTTP_LOG_BODY_MODE=string` keeps bodies as
JSON-encoded strings (bounded field growth for an app with wild schemas);
querying then goes through `| unpack_json from req_body fields (amount)
| filter amount:>100`, and substring search (`req_body:insufficient_balance`)
always works in both modes.

Field notes:

- `url` keeps the query string with sensitive params redacted per key;
  `path` is the bare path; `route` is the Express/Nest route template when
  available (absent for Remix).
- `host` is the Host header (which domain was hit); `ip` is the first
  `x-forwarded-for` hop, falling back to the socket address.
- `trace_id`/`span_id` come from the active OTel span (or the caller's
  `traceparent`) — paste into the traces UI to see the request's spans.
- Bodies are JSON-parsed and field-redacted (`token`, `password`, `otp`, … →
  `[REDACTED]`), capped at `HTTP_LOG_BODY_MAX` (4 KiB default,
  `*_truncated:true` flags when hit). Compressed responses log as
  `"[gzip N bytes]"`; non-JSON responses as `"[<type> N bytes]"`.
- Headers are an allowlist (`host`, `content-type`, `content-length`,
  `user-agent`, `referer`, `origin`, `accept-language`, `x-forwarded-for`,
  `userid`, `operatorid`, `traceparent`) — auth headers can never leak.
- Client disconnects log `status:499` with `aborted:true`.
- The Fly log envelope adds `fly.app.name`, `fly.app.instance`, `region` and
  the machine `host` around every line, which is how those become
  VictoriaLogs stream fields alongside `logger=http` — the JSON above is
  what the app writes to stdout.

## Configuration (env, per app in fly.toml)

| Variable | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — (**required** for tracing) | OTLP base path; unset = tracing off |
| `OTEL_SERVICE_NAME` | `FLY_APP_NAME`, else package.json name | Service name on spans + http lines |
| `OTEL_TRACES_EXPORTER` | `otlp` | `console` for local debugging |
| `OTEL_NODE_RESOURCE_DETECTORS` | `env,host,os,process` | Standard OTel resource detectors |
| `OTEL_TRACES_SAMPLER_ARG` | `0.1` | Head-sampling ratio (parent-based) |
| `OTEL_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | No spans for these paths (replaces list; `prefix/` = subtree) |
| `OTEL_SDK_DISABLED` | — | `true` kills tracing |
| `HTTP_LOG` | `on` on Fly, `off` elsewhere | Master switch for http logging |
| `HTTP_LOG_PAYLOAD` | `errors` | Attach headers+bodies to the line: `errors` (4xx/5xx + slow) \| `always` \| `off` |
| `HTTP_LOG_SLOW_MS` | `1000` | "errors" tier also fires above this duration |
| `HTTP_LOG_BODY_MAX` | `4096` | Bytes kept per body (request and response) |
| `HTTP_LOG_BODY_MODE` | `object` | JSON bodies as nested fields (`object`) or JSON strings (`string`) |
| `HTTP_LOG_PAYLOAD_ROUTES` | — | Comma path-prefixes that always get payloads |
| `HTTP_LOG_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Paths logged not at all |
| `LOG_LEVEL` | `info` | Level of the exported app `logger` |

**Resource attribute defaults** are composed into `OTEL_RESOURCE_ATTRIBUTES`
at startup for any key the app didn't set itself (per-app overrides always
win): `cloud.provider=fly_io` (when on Fly), `cloud.region=$FLY_REGION`
(else `auto`), `service.instance.id=$FLY_MACHINE_ID` (else `NA`),
`service.version=$FLY_IMAGE_REF` (else `NA`), `deployment.environment.name`,
and the legacy `fly.region` key existing dashboards query. When Fly env vars
are missing (local runs, other platforms), a single `console.warn` at
registration lists each one with the fallback now in effect, and ends with
ready-to-paste override lines — one complete `OTEL_RESOURCE_ATTRIBUTES`
value carrying every missing key, comma-separated:

```
[telemetry] Fly env not available (warned once, at registration). Fly sets these automatically at runtime; elsewhere provide them explicitly:
  - FLY_APP_NAME not set -> service.name falls back to "my-api"
  - FLY_REGION not set -> cloud.region falls back to "auto"
  - FLY_MACHINE_ID not set -> service.instance.id falls back to "NA"
  - FLY_IMAGE_REF not set -> service.version falls back to "NA"
  To set them, add to the environment (OTEL_RESOURCE_ATTRIBUTES is ONE variable, comma-separated — keep keys you already set and replace the <placeholders>):
    OTEL_SERVICE_NAME=<service-name>
    OTEL_RESOURCE_ATTRIBUTES=cloud.region=<region>,service.instance.id=<machine-or-host-id>,service.version=<version-or-image>
```

Variables whose value the app already supplied via an override are not
warned about (and don't appear in the example line), and nothing telemetry
logs is per-request — boot-time lines only.

Redaction is field-level and shared by both halves (`src/redact.js`, ported
from softstudio-bo): sensitive keys (`password`, `token`, `hashkey`, `cvv`, …)
are replaced with `[REDACTED]` inside bodies and query strings; headers are an
allowlist; the `ScrubbingSpanProcessor` enforces the same policy on every span
as the last line of defence.

## Known limitations

- **Route templates**: `route` is populated for Express/Nest (`req.route` at
  response close). Remix requests log the raw redacted path — the Remix route
  id is not visible at the transport layer.
- **Compressed responses** are logged as `[gzip N bytes]` placeholders, never
  decoded (a capped prefix of a compressed stream can't be). If a route's
  JSON bodies matter in payload logs, don't compress them app-side.
- **Response bodies are captured only for JSON content types** — Remix HTML /
  streamed documents are excluded by design; access lines still cover them.
- **ESM loader hook** (`register.mjs`): needed for express/winston spans in
  ESM apps; uses `module.register` + import-in-the-middle's message channel.
  If it misbehaves, the documented fallback is adding
  `--experimental-loader=@opentelemetry/instrumentation/hook.mjs` to
  `NODE_OPTIONS`. Pilot on the Remix app before fleet rollout.
- Node **>= 22** required (the http logger is pure `diagnostics_channel`).

## Publishing

The package is **public on npmjs.org** (`publishConfig.access=public`,
MIT-licensed). One-time setup: create the `insidebeehive` org on npmjs.com
and add an npm automation token as the `NPM_TOKEN` Actions secret in this
repo. After that, publishing happens from this directory when a
`telemetry-v*` tag is pushed (`.github/workflows/publish-telemetry.yml`):

```sh
cd telemetry
npm version patch            # bumps package.json, e.g. 0.1.1
git commit -am "telemetry: 0.1.1 — <what changed>"
git tag telemetry-v0.1.1
git push origin master telemetry-v0.1.1
```

Dependencies are pinned exact so two apps on the same package version behave
identically; bumps to the OTel stack happen here, ship as one release, and
reach apps as a one-line lockfile update.
