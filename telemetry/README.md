# @insidebeehive/telemetry

Zero-code telemetry for BetStudio Node services on Fly.io. One dependency,
one env var, no app code:

- **Traces** — OpenTelemetry auto-instrumentation (http, express, nest, pg,
  ioredis, mongodb), parent-based sampling at 10%, exported to VictoriaTraces
  (`bhgrafana.flycast:10428`). Winston/pino log records get `trace_id`
  injected automatically — the log↔trace join key.
- **HTTP logging** — one `http.access` JSON line per request (100%, method /
  path / route / status / duration / trace_id) and policy-gated `http.payload`
  lines (redacted headers + capped bodies). Lines carry `logger=http`, the
  VictoriaLogs stream field the Grafana dashboards query, and reach
  VictoriaLogs via stdout → Fly logs → Vector. No winston coupling.

Both halves are fail-safe (a telemetry bug never stops an app booting),
auto-enable only on Fly (`FLY_APP_NAME` present), and stay silent in local
dev, tests and CI.

## Integration (per app)

1. Install (once per repo — needs the org registry for the `@insidebeehive`
   scope in `.npmrc`):

   ```
   @insidebeehive:registry=https://npm.pkg.github.com
   ```

   ```sh
   npm install @insidebeehive/telemetry
   ```

2. Activate in `fly.toml` — the same line for NestJS (CJS) and Remix (ESM):

   ```toml
   [env]
     NODE_OPTIONS = "--import @insidebeehive/telemetry/register"
     # optional — defaults to FLY_APP_NAME:
     # OTEL_SERVICE_NAME = "core-stage"
   ```

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

## Configuration (env, per app in fly.toml)

| Variable | Default | Meaning |
|---|---|---|
| `OTEL_SERVICE_NAME` | `FLY_APP_NAME` | Service name on spans + http lines |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | org collector (on Fly) | OTLP base path; unset off-Fly = tracing off |
| `OTEL_TRACES_SAMPLER_ARG` | `0.1` | Head-sampling ratio (parent-based) |
| `OTEL_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | No spans for these paths (replaces list; `prefix/` = subtree) |
| `OTEL_SDK_DISABLED` | — | `true` kills tracing |
| `HTTP_LOG` | `on` on Fly, `off` elsewhere | Master switch for http logging |
| `HTTP_LOG_PAYLOAD` | `errors` | `errors` (4xx/5xx + slow) \| `always` \| `off` |
| `HTTP_LOG_SLOW_MS` | `1000` | "errors" tier also fires above this duration |
| `HTTP_LOG_BODY_MAX` | `4096` | Bytes kept per body (request and response) |
| `HTTP_LOG_PAYLOAD_ROUTES` | — | Comma path-prefixes that always get payloads |
| `HTTP_LOG_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Paths logged not at all |

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

The package publishes from this directory to GitHub Packages when a
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
