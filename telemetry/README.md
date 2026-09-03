# @insidebeehive/telemetry

Zero-code observability for Node.js services: OpenTelemetry **traces**,
structured **HTTP request logging**, and a preconfigured **app logger** with
an audit level — one dependency, one env var, no application code.

Built for stacks that collect stdout JSON logs (e.g. Vector → VictoriaLogs,
or any JSON-aware log pipeline) and OTLP traces. Auto-configures on
[Fly.io](https://fly.io) (service name, region, instance come from the
platform env); works anywhere Node ≥ 22 runs.

- **Traces** — OTel auto-instrumentation (http, express, nest, pg, ioredis,
  mongodb), parent-based sampling (10% default), OTLP export to the endpoint
  you set. Winston/pino log records get `trace_id` injected automatically —
  the log↔trace join key.
- **HTTP logging** — exactly one `http.access` JSON line per request (100%:
  method / path / route / url / http_host / status / duration_ms / ip /
  trace_id). When the payload policy fires, the **same line** also carries
  redacted headers and capped JSON bodies, so a failed transaction's record
  is self-contained evidence. Every line has `logger=http` for stream-level
  separation from app logs.
- **App logger** — `import logger from "@insidebeehive/telemetry/logger"`
  and `logger.info(...)` anywhere: JSON in production, pretty locally,
  `logger=app`, `service` stamped, `trace_id` auto-injected inside requests.
  Includes an `audit` level that no log-level setting can silence.

Everything is fail-safe — a telemetry bug never stops an app booting.
Tracing stays off until an endpoint is configured, so dev and CI emit no
spans. HTTP logging is **on by default everywhere**, full (redacted)
payloads included; set `HTTP_LOG=off` in local dev or test runs if the
lines are unwanted there.

## Setup

1. Install:

   ```sh
   npm install @insidebeehive/telemetry
   ```

2. Activate via environment — no code changes. The same line works for CJS
   (e.g. NestJS `dist/`) and ESM (e.g. Remix/Vite server builds):

   ```sh
   NODE_OPTIONS="--import @insidebeehive/telemetry/register"
   OTEL_EXPORTER_OTLP_ENDPOINT="http://your-collector:4318"   # base path; SDK appends /v1/traces
   ```

   On Fly.io that's the `[env]` block of `fly.toml`:

   ```toml
   [env]
     NODE_OPTIONS = "--import @insidebeehive/telemetry/register"
     OTEL_EXPORTER_OTLP_ENDPOINT = "http://your-collector.internal:4318"
     # optional — defaults to FLY_APP_NAME, then your package.json name:
     # OTEL_SERVICE_NAME = "my-api"
   ```

3. Keep the container CMD a direct `node <entry>` (`node dist/main.js`,
   `node ./build/server/index.js`) — not `npm start`, which would boot the
   SDK inside npm's own node process too.

Verify: boot logs show `[telemetry] tracing enabled -> …` and
`[telemetry] http logger enabled …`; your log store receives
`logger=http` and `logger=app` JSON lines; your tracing UI shows spans for
the service.

**Prefer explicit code over NODE_OPTIONS?** Same package, first line of the
entrypoint: `require("@insidebeehive/telemetry").init()` (or
`import "@insidebeehive/telemetry/register"`). All styles are guarded —
double activation is a no-op. **Migrating a service that already has its own
OTel bootstrap:** remove it when enabling this package, or the process is
double-instrumented. **This replaces per-app access logging** — disable
morgan / framework request-loggers, or the `logger=http` stream gets doubled
entries.

## App logger

No per-app logger setup — import and log, from any file:

```js
import logger from "@insidebeehive/telemetry/logger";
// equivalent named form: import { logger } from "@insidebeehive/telemetry";

logger.info("order placed", { orderId, amount });
const walletLog = logger.child({ module: "wallet" });   // plain winston child

try {
  await capturePayment(order);
} catch (err) {
  // custom message + full stack + context fields, all in one line:
  logger.error("payment capture failed", { err, orderId: order.id });
  // bare form works too: logger.error(err)
}
```

In production each call is one JSON line on stdout:

```json
{"level":"info","message":"order placed","timestamp":"2026-09-01T10:24:03.512Z","logger":"app","service":"my-api","orderId":"o_991","amount":250,"trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7"}
```

Locally it pretty-prints with colors instead. Notes:

- `logger=app` is stamped via winston `defaultMeta` — the stream-level
  partner of the HTTP logger's `logger=http`, so app logs and the HTTP
  firehose are queryable separately.
- `trace_id`/`span_id` appear automatically on lines logged inside a request
  — the pivot between an app log, its `http.access` line and its spans.
- `LOG_LEVEL` env sets the level (default `info`). It gates **only this app
  logger** — the HTTP logger writes to stdout directly and always emits.
- Errors anywhere in the meta are serialized with message + stack
  (`{ err }` above); plain winston would log them as `{}`.
- **The app logger logs exactly what you pass it** — the automatic
  credential redaction applies to HTTP capture, query strings and spans,
  not to your own `logger.info("x", { ... })` meta. Don't put passwords,
  tokens or API keys in log fields.
- **Uncaught exceptions and unhandled rejections** are captured as one
  structured JSON line (same format, stack included), then the process exits
  so your platform restarts it. Handlers are registered at activation, so
  even a boot crash is covered.
- NestJS apps can route Nest's own logging through it:
  `WinstonModule.createLogger({ instance: logger })` (nest-winston).
- Existing app loggers keep working and still get trace injection; the
  export is simply the zero-config path. It's built lazily, so pino-only
  apps never load winston.
- **TypeScript works out of the box** — `.d.ts` files ship with the package;
  `logger` is typed as winston's `Logger` plus `audit`.

### Audit level

Events that must never be lost to log-level configuration (who did what to
which entity) use the `audit` level on the same logger:

```js
logger.audit("order.refunded",   { actor: adminId, orderId, amount, reason });
logger.audit("balance.adjusted", { actor: "system", userId, delta });
```

`audit` is a custom winston level at **priority 0 — above `error`** — so no
`LOG_LEVEL` value can silence it. Lines stay in the `logger=app` stream with
`level=audit`. Suggested shape: a dotted `entity.action` message plus
`actor` and entity ids as fields.

Durability note: a log pipeline is best-effort (lines in flight during a
restart can be lost) and bounded by your retention. For regulatory audit
trails keep the database as the system of record; this level is the fast,
queryable, trace-correlatable copy.

## The HTTP log line

**Exactly one line per request**, message type `http.access`. A normal
request under the default policy:

```json
{"level":"info","message":"http.access","ts":"2026-09-01T10:24:03.512Z","logger":"http","service":"my-api","method":"POST","path":"/api/v1/orders","route":"/api/v1/orders","url":"/api/v1/orders?sku=s_123&token=[REDACTED]","http_host":"api.example.com","status":201,"duration_ms":42.7,"ip":"203.0.113.7","trace_id":"4bf92f3577b34da6a3ce929d0e0e4736","span_id":"00f067aa0ba902b7","trace_sampled":true,"res_bytes":86}
```

By default (`HTTP_LOG_PAYLOAD=always`) **every** line carries the evidence
— redacted headers and capped bodies on the same line. Set `errors` to
attach them only on 4xx/5xx and slow requests, or `off` for bare access
lines:

```json
{"…all fields above…":"…","status":422,"payload":true,"req_headers":{"host":"api.example.com","content-type":"application/json","content-length":"64","user-agent":"…"},"req_body":"{\"amount\":250,\"sku\":\"s_123\",\"token\":\"[REDACTED]\"}","res_headers":{"content-type":"application/json"},"res_body":"{\"error\":\"insufficient_balance\",\"balance\":120}"}
```

Field notes:

- `url` keeps the query string, with credential params (`token`,
  `session*`, `*key`, …) redacted per key and everything else — refs,
  amounts, ids — verbatim; `path` is the bare path, logged verbatim;
  `route` is the Express/Nest route template when available.
- `http_host` is the Host header; `ip` is the first `x-forwarded-for` hop.
- `runtime` is `node` or `bun` (the sibling .NET package emits `dotnet`) —
  constant per service, so it is cheap to make a log-store *stream* field
  and group the fleet by runtime; it also pins which capture path
  (`diagnostics_channel` vs Bun's server-wrap) produced the line.
- `trace_id`/`span_id` come from the active span (or the caller's
  `traceparent`); `trace_sampled` says whether stored spans exist for it.
- **Bodies are JSON-parsed and credential-redacted** (`password`, `otp`,
  `session*`, `token`, `*key`, `secret`, `signature` → `[REDACTED]`;
  every other field — amounts, ids, card/account numbers, refs — logged
  verbatim), capped at `HTTP_LOG_BODY_MAX` with
  `*_truncated` flags, and logged as **one JSON-encoded string field** by
  default — keeping each line's field set lean. Substring search works
  directly (`req_body:some_value`), and stores like VictoriaLogs unpack at
  query time: `| unpack_json from req_body fields (amount) | filter
  amount:>100`. Set `HTTP_LOG_BODY_MODE=object` to log bodies as nested
  objects instead — the store then indexes each key as its own field
  (`req_body.amount:>100` directly); best for apps with stable,
  frequently-queried body schemas.
- **Credentials survive nothing**: bodies that fail JSON parsing
  (truncated past the cap, malformed) get a best-effort key/value scrub
  with the same credential key set — a `token` whose value was sliced by
  the byte cap is still redacted by key; urlencoded bodies get per-key
  redaction. NUL bytes are stripped before scrubbing, so NUL-interleaved
  text (UTF-16 bytes behind a mislabeled charset) can't smuggle credential
  values past the scrubbers.
- Compressed, multipart and other binary bodies are never decoded — they
  log as size placeholders (`"[gzip 1234 bytes]"`,
  `"[multipart/form-data 1234 bytes]"`) on both the request and response
  side. Bodies in a declared non-ASCII-compatible charset (`utf-16le`, …)
  get the same placeholder treatment, since the scrubbers can't see
  through those bytes. Response bodies are captured for JSON content types
  only (streamed HTML documents stay out). Headers are an allowlist —
  `Cookie` and `Authorization` are structurally never logged — and logged
  header values are capped at 512 chars; `referer`, being a URL, gets the
  same per-key query redaction as the `url` field.
- Client disconnects log `status:499` with `aborted:true`; `payload:true`
  marks enriched lines.

## Configuration (env)

| Variable | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — (**required** for tracing) | OTLP base path; unset = tracing off |
| `OTEL_SERVICE_NAME` | `FLY_APP_NAME`, else package.json name | Service name on spans + log lines |
| `OTEL_TRACES_EXPORTER` | `otlp` | `console` for local debugging |
| `OTEL_NODE_RESOURCE_DETECTORS` | `env,host,os,process` | Standard OTel resource detectors |
| `OTEL_TRACES_SAMPLER_ARG` | `0.1` | Head-sampling ratio (parent-based) |
| `OTEL_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | No spans for these paths (`prefix/` = subtree) |
| `OTEL_SDK_DISABLED` | — | `true` kills tracing |
| `HTTP_LOG` | `on` | Master switch for HTTP logging (set `off` for local dev/tests) |
| `HTTP_LOG_PAYLOAD` | `always` | Attach headers+bodies: `always` \| `errors` (4xx/5xx + slow) \| `off` |
| `HTTP_LOG_SLOW_MS` | `1000` | "errors" tier also fires above this duration |
| `HTTP_LOG_BODY_MAX` | `4096` | Bytes kept per body (request and response) |
| `HTTP_LOG_BODY_MODE` | `string` | JSON bodies as JSON strings (`string`) or nested fields (`object`) |
| `HTTP_LOG_PAYLOAD_ROUTES` | — | Comma path-prefixes that always get payloads |
| `HTTP_LOG_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Paths logged not at all |
| `LOG_LEVEL` | `info` | Level of the exported app `logger` (audit exempt) |
| `LOGTAIL_URL` + `LOGTAIL_TOKEN` | — | When both set, app-logger lines (only — never http lines) also ship to Logtail/BetterStack: batched, fire-and-forget, drops on outage (stdout stays the source of truth) |

**Invalid env values fail loud, not silent**: a typo in `LOG_LEVEL`,
`HTTP_LOG_PAYLOAD`, `HTTP_LOG_BODY_MODE`, `HTTP_LOG_BODY_MAX`,
`HTTP_LOG_SLOW_MS`, `OTEL_TRACES_SAMPLER_ARG` (must be a number in 0..1) or
`LOGTAIL_URL` logs a startup warning and falls back to the default — it can
never quietly disable capture or the audit level. Mode values are
case-insensitive (`HTTP_LOG=OFF` works), as is content-type matching
(`APPLICATION/JSON; charset=UTF-8` is captured). Note that setting
`HTTP_LOG_IGNORE_PATHS`/`OTEL_IGNORE_PATHS` **replaces** the default list;
re-include `/health` etc. when adding your own entries.

**Resource attribute defaults** are composed into `OTEL_RESOURCE_ATTRIBUTES`
for any key you didn't set (your values always win): `cloud.provider`,
`cloud.region`, `service.instance.id`, `service.version`,
`deployment.environment.name` — derived from the platform env where
available, with a single startup `console.warn` listing anything missing and
the exact override line to set.

**Redaction philosophy: logs are evidence.** Business data — amounts, ids,
card and account numbers, bank/UPI refs — is logged verbatim; when an
incident needs replaying, the record is complete. Only material that
grants access is redacted, by key, everywhere (bodies, query strings,
span attributes): passwords/OTPs/PINs, session ids and tokens, API keys
and secrets, webhook signatures. `Cookie` and `Authorization` headers are
never logged at all (allowlist). A scrubbing span processor enforces the
same policy on every span as the last line of defence.

## Runtime support

| Runtime | Traces | HTTP logging | App/audit logger |
|---|---|---|---|
| Node ≥ 22, CJS | ✓ | ✓ (`diagnostics_channel`) | ✓ |
| Node ≥ 22, ESM | ✓ (loader hook via `--import`) | ✓ | ✓ |
| Bun ≥ 1.x | — skipped cleanly (OTel Node SDK unsupported; trace ids still arrive via the caller's `traceparent`) | ✓ (server-wrap fallback, auto-selected) | ✓ |

**Bun activation**: Bun ignores `NODE_OPTIONS`, so activate in the CMD —
`bun --require @insidebeehive/telemetry/register server.ts` — or in
`bunfig.toml`: `preload = ["@insidebeehive/telemetry/register"]`.

## Known limitations

- **Route templates**: `route` is populated for Express/Nest. Other
  frameworks log the raw redacted path.
- **Compressed bodies** are never decoded (a capped prefix of a compressed
  stream can't be) — size placeholders instead.
- **Card numbers and other business data are NOT redacted** — a
  deliberate policy (versions 0.1.7–0.1.11 carried PCI-style Luhn PAN
  scrubbing; it was removed because its false positives redacted the
  transaction refs the logs exist to keep). If your compliance posture
  requires PAN-free logs, don't route card-bearing payloads through
  payload logging (`HTTP_LOG_PAYLOAD=errors` + route filters), or pin
  0.1.11.
- **Credential-key matching is ASCII-based** (keys are normalised to
  alphanumerics before matching); a key spoofed with Unicode homoglyphs
  won't match. Legitimate clients don't do this.
- **Response headers set via `res.writeHead(status, headersObj)`** may not
  appear in `res_headers` (Node fast-paths them past the header map the
  logger reads). Headers set with `res.setHeader()` are always captured.
- **Memory**: full-payload logging + tracing adds working-set overhead
  (roughly 100–200 MB RSS under sustained load in QA, plateauing — not a
  leak). Budget for it on the smallest machine sizes.
- Node **>= 22** required (the HTTP logger is pure `diagnostics_channel`).

## License

MIT — source, issues and examples at
[insidebeehive/fly-telemetry](https://github.com/insidebeehive/fly-telemetry).
