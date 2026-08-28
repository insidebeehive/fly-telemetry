"use strict";
/**
 * Platform telemetry preload — the ONLY wiring an app needs is:
 *
 *   NODE_OPTIONS="--require @beehive/telemetry-preload"
 *
 * (or `--require /path/to/telemetry-preload` when vendored into an image).
 * Loaded before any application module so the OTel SDK can patch http,
 * express, pg, ioredis and winston before they are first required.
 *
 * Scope (deliberate): OpenTelemetry TRACES ONLY — spans at
 * OTEL_TRACES_SAMPLER_ARG (default 0.1), traceparent propagation on 100% of
 * requests, and winston trace_id injection so app logs stay correlatable.
 * Master switch: OTEL_EXPORTER_OTLP_ENDPOINT unset = fully off.
 * (HTTP request/response payload logging was prototyped here and removed —
 * see repo history — pending a separate decision on how to capture it.)
 *
 * Observability must never be the reason a service fails to boot.
 */
try {
  require("./tracing");
} catch (error) {
  console.error("[telemetry-preload] tracing init failed, continuing without it", error);
}
