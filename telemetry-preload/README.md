# @beehive/telemetry-preload

Zero-app-code **OpenTelemetry tracing** for BetStudio Node services. One
preload gives a service:

- `traceparent` propagation on **100%** of requests — the ID that stitches
  PGS → bo-api → core into one journey;
- spans (HTTP server/client, express routes, pg, ioredis) exported to
  VictoriaTraces at the sampled ratio (default **10%**);
- `trace_id`/`span_id` injected into every winston log record, so ordinary
  app logs stay correlatable in VictoriaLogs.

Scope is deliberately **traces only**. HTTP request/response payload logging
was prototyped here and removed (see repo history) pending a separate
decision on how to capture it.

Fails open: any init error logs once and the app boots normally.

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

That endpoint is the master switch — unset means tracing is off (local dev,
CI, tests). `OTEL_SERVICE_NAME` defaults to `FLY_APP_NAME`.

## Env reference

| Var | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset = off)* | Base OTLP endpoint; SDK appends `/v1/traces` |
| `OTEL_TRACES_SAMPLER_ARG` | `0.1` | Trace sample ratio (parent-based: whole requests, all hops) |
| `OTEL_SERVICE_NAME` | `$FLY_APP_NAME` | Service name on spans |
| `OTEL_IGNORE_PATHS` | `/,/health,/healthz,/favicon.ico` | Never traced (exact, or `prefix/`) |
| `OTEL_SDK_DISABLED` | | `true` kills tracing regardless of endpoint |
| `OTEL_LOG_LEVEL` | `error` | SDK diagnostics verbosity |

## PII posture

Spans never carry request/response headers or bodies. pg and mongodb run with
`enhancedDatabaseReporting` off (no bound values in `db.statement`), ioredis
records command+key only, and `ScrubbingSpanProcessor` (using `redact.js`,
ported from softstudio-bo's proven `redact.util.ts`) structurally strips or
redacts anything sensitive that an upstream default change might reintroduce.

## Migrating a service that already has in-repo OTel

Running this preload **and** an in-repo SDK double-instruments the process.
At cutover:

- `softstudio-bo/apps/api`: delete `src/tracing.ts` and the `--require
  ./dist/tracing.js` from Dockerfile CMD + `start:prod`. Sentry stays
  app-owned (its init moves back to a plain import if still wanted).
- `softstudio-core-v2/apps/core`: delete `src/common/utils/otel-instrument.ts`
  (keep `sentry-instrument.ts` minus the otel import) and the
  `GRAFANA_OTLP_*` secrets.

## Runtime coverage

This preload is Node-only. .NET services use the OpenTelemetry CLR
auto-instrumentation (env-driven, also zero-code for traces). Go and Rust
have no injection mechanism — they take a one-line import of a
platform-owned library emitting the same contract (traceparent + OTLP to the
same endpoint).
