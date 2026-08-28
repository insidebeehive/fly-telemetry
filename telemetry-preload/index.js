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
 * Two independent halves, each with its own kill switch:
 *   tracing.js     — OTel spans at OTEL_TRACES_SAMPLER_ARG (default 0.1) +
 *                    traceparent propagation + winston trace_id injection.
 *                    Master switch: OTEL_EXPORTER_OTLP_ENDPOINT unset = off.
 *   http-logger.js — http.access (INFO, 100%) + http.payload (DEBUG, policy)
 *                    JSON lines on stdout -> Fly log stream -> VictoriaLogs.
 *                    Master switch: HTTP_LOG=off.
 *
 * Observability must never be the reason a service fails to boot.
 */
try {
  require("./tracing");
} catch (error) {
  console.error("[telemetry-preload] tracing init failed, continuing without it", error);
}

try {
  require("./http-logger");
} catch (error) {
  console.error("[telemetry-preload] http logger init failed, continuing without it", error);
}
