"use strict";
/**
 * @insidebeehive/telemetry — one package, two halves, two activation styles.
 *
 * Zero-code (the default; nothing in the app changes):
 *
 *   NODE_OPTIONS="--import @insidebeehive/telemetry/register"
 *
 * works for CJS (NestJS dist/) and ESM (Remix/Vite server builds) alike —
 * the export map routes --require to register.js and --import to
 * register.mjs, which also installs the ESM loader hook.
 *
 * Programmatic (for apps that prefer an explicit in-code bootstrap):
 *
 *   require("@insidebeehive/telemetry").init();   // first line of main.ts
 *
 * Both styles run this init(); the global symbol makes double activation a
 * no-op, so an app that has the NODE_OPTIONS flag AND calls init() is still
 * instrumented exactly once.
 *
 * App logging is exported too — no per-app winston setup:
 *
 *   const { logger } = require("@insidebeehive/telemetry");
 *   logger.info("bet placed", { betId, amount });
 *
 * Lines land in the logger=app VictoriaLogs stream (http traffic is
 * logger=http), with service and, inside requests, trace_id stamped.
 *
 * The halves, each with its own kill switch:
 *   src/tracing.js     — OTel spans (parent-based sampling, default 0.1),
 *                        traceparent propagation, winston/pino trace_id
 *                        injection. Off until OTEL_EXPORTER_OTLP_ENDPOINT is
 *                        set (no baked default), and when OTEL_SDK_DISABLED.
 *   src/http-logger.js — one http.access JSON line per request (100%) on
 *                        stdout -> Fly logs -> VictoriaLogs, logger=http
 *                        stream; headers+bodies attach to the same line per
 *                        HTTP_LOG_PAYLOAD policy. Auto-on on Fly only; off
 *                        when HTTP_LOG=off, forced locally with HTTP_LOG=on.
 * Local dev and CI stay clean with zero setup either way.
 *
 * Observability must never be the reason a service fails to boot.
 */

const INSTALLED = Symbol.for("beehive.telemetry.installed");

function init() {
  if (global[INSTALLED]) return;
  global[INSTALLED] = true;

  try {
    require("./src/tracing").startTracing();
  } catch (error) {
    console.error("[telemetry] tracing init failed, continuing without it", error);
  }

  try {
    require("./src/http-logger").install();
  } catch (error) {
    console.error("[telemetry] http logger init failed, continuing without it", error);
  }
}

// The zero-config app logger (winston, logger=app stream field, trace_id
// injection inside requests). Lazy: winston loads on first logger use.
const { logger } = require("./src/app-logger");

module.exports = { init, logger };
