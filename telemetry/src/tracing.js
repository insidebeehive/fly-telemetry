"use strict";
/**
 * OpenTelemetry bootstrap — exports traces to the org telemetry app
 * (VictoriaTraces behind bhgrafana.flycast:10428).
 *
 * Ported from softstudio-bo apps/api/src/tracing.ts (which proved this tuning
 * in production against Grafana Cloud) via the retired telemetry-preload.
 * Differences from that file:
 *   - service name defaults to FLY_APP_NAME, then the app's package.json name
 *   - default sample ratio is 0.1 (the platform decision: spans are the 10%
 *     code-level detail; the 100% request record lives in http-logger.js)
 *   - the OTLP endpoint is ALWAYS explicit per-app config (deployment
 *     concern, not library code): OTEL_EXPORTER_OTLP_ENDPOINT unset means
 *     tracing is off and no SDK objects are constructed — which is also what
 *     keeps local dev and CI clean
 *   - remaining defaults are written into the STANDARD OTel env vars when
 *     unset (exporter=otlp, protocol=http/protobuf, resource detectors,
 *     Fly-derived resource attributes), so overriding any of them in
 *     fly.toml behaves exactly like stock OpenTelemetry
 *   - Sentry stays app-owned; this package does not touch it
 *
 * NOTE for services being migrated: delete any in-repo OTel bootstrap
 * (tracing.ts / otel-instrument.ts) when this package is enabled, or the
 * process will be double-instrumented. Apps that want an explicit in-code
 * bootstrap instead of NODE_OPTIONS call init() from the package root —
 * same code path, guarded against double activation.
 */

const { resolveServiceName } = require("./service-name");

const truthy = (value) => value === "true" || value === "1";

/**
 * Paths that would otherwise dominate trace volume while telling us nothing:
 * Fly health checks and the root liveness probe. Entries match EXACTLY unless
 * they end in "/" (subtree prefix); bare "/" is always an exact match.
 * Extend per-service with OTEL_IGNORE_PATHS (comma-separated, replaces list).
 */
const DEFAULT_IGNORED_PATHS = ["/", "/health", "/healthz", "/favicon.ico"];

function ignoredPaths() {
  const configured = process.env.OTEL_IGNORE_PATHS;
  if (!configured) return DEFAULT_IGNORED_PATHS;
  return configured.split(",").map((entry) => entry.trim()).filter(Boolean);
}

function isIgnoredPath(target, ignored) {
  if (typeof target !== "string") return false;
  const path = target.split("?")[0];
  return ignored.some((ignore) => {
    if (ignore.endsWith("/") && ignore !== "/") return path.startsWith(ignore);
    return path === ignore;
  });
}

function startTracing() {
  if (truthy(process.env.OTEL_SDK_DISABLED)) {
    console.log("[telemetry] tracing disabled via OTEL_SDK_DISABLED");
    return;
  }

  // Bun: the OpenTelemetry Node SDK's module hooks are not supported and
  // starting it wedges the http server (verified empirically on bun 1.4).
  // Skip tracing cleanly — http + app logging still work; http lines fall
  // back to the caller's traceparent header for trace ids.
  if (process.versions && process.versions.bun) {
    console.log("[telemetry] tracing skipped: Bun runtime is not supported by the OTel Node SDK (http/app logging active)");
    return;
  }

  if (!process.env.OTEL_TRACES_EXPORTER) {
    process.env.OTEL_TRACES_EXPORTER = "otlp";
  }
  // This package ships TRACES only: metrics arrive via Fly's NATS stream and
  // logs via stdout->Vector. The SDK's spec defaults would start OTLP
  // exporters for all three signals against a traces-only endpoint (observed:
  // PeriodicExportingMetricReader export failures at shutdown).
  if (!process.env.OTEL_METRICS_EXPORTER) {
    process.env.OTEL_METRICS_EXPORTER = "none";
  }
  if (!process.env.OTEL_LOGS_EXPORTER) {
    process.env.OTEL_LOGS_EXPORTER = "none";
  }
  const consoleMode = process.env.OTEL_TRACES_EXPORTER === "console";
  const endpoint = process.env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT || process.env.OTEL_EXPORTER_OTLP_ENDPOINT;

  // The endpoint is the master switch, and is deliberately never defaulted —
  // where traces go is deployment config, set per app in fly.toml. No
  // endpoint: no tracing, no SDK objects constructed at all.
  if (!endpoint && !consoleMode) {
    console.log("[telemetry] tracing disabled (set OTEL_EXPORTER_OTLP_ENDPOINT to enable)");
    return;
  }

  // VictoriaTraces (and Grafana's gateway) speak protobuf over HTTP. Set
  // before the exporter is constructed so the standard env config applies.
  if (!process.env.OTEL_EXPORTER_OTLP_PROTOCOL) {
    process.env.OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf";
  }
  if (!process.env.OTEL_NODE_RESOURCE_DETECTORS) {
    process.env.OTEL_NODE_RESOURCE_DETECTORS = "env,host,os,process";
  }

  // --- Fly-derived resource attribute defaults ---
  // Composed INTO OTEL_RESOURCE_ATTRIBUTES (only keys the app didn't set),
  // then materialised by the standard env detector — so a per-app override
  // in fly.toml wins over every default here, exactly like stock OTel.
  const fly = {
    FLY_APP_NAME: process.env.FLY_APP_NAME,
    FLY_REGION: process.env.FLY_REGION,
    FLY_MACHINE_ID: process.env.FLY_MACHINE_ID,
    FLY_IMAGE_REF: process.env.FLY_IMAGE_REF,
  };
  const providedKeys = new Set(
    String(process.env.OTEL_RESOURCE_ATTRIBUTES || "")
      .split(",")
      .map((pair) => pair.split("=")[0].trim())
      .filter(Boolean),
  );

  // Missing Fly vars are warned about ONCE, here at registration (this
  // function runs a single time per process, guarded by init()) — never per
  // request. The warning ends with ready-to-paste override lines: one
  // complete OTEL_RESOURCE_ATTRIBUTES value carrying every missing key,
  // comma-separated, so a reader sees in one place exactly what to set.
  // A variable whose value the app already supplied via an override is not
  // warned about — nothing is missing from the telemetry then.
  const FLY_VAR_HELP = [
    {
      name: "FLY_APP_NAME",
      feeds: "service.name",
      fallback: () => `"${resolveServiceName()}"`,
      placeholder: null, // service.name has its own env var, not a resource attribute
      overridden: () => Boolean(process.env.OTEL_SERVICE_NAME),
    },
    {
      name: "FLY_REGION",
      feeds: "cloud.region",
      fallback: () => '"auto"',
      placeholder: "<region>",
      overridden: () => providedKeys.has("cloud.region"),
    },
    {
      name: "FLY_MACHINE_ID",
      feeds: "service.instance.id",
      fallback: () => '"NA"',
      placeholder: "<machine-or-host-id>",
      overridden: () => providedKeys.has("service.instance.id"),
    },
    {
      name: "FLY_IMAGE_REF",
      feeds: "service.version",
      fallback: () => '"NA"',
      placeholder: "<version-or-image>",
      overridden: () => Boolean(process.env.OTEL_SERVICE_VERSION) || providedKeys.has("service.version"),
    },
  ];
  const missingFly = FLY_VAR_HELP.filter((entry) => !fly[entry.name] && !entry.overridden());
  if (missingFly.length) {
    const lines = missingFly.map((entry) => `  - ${entry.name} not set -> ${entry.feeds} falls back to ${entry.fallback()}`);
    const fixes = [];
    if (missingFly.some((entry) => entry.name === "FLY_APP_NAME")) {
      fixes.push("    OTEL_SERVICE_NAME=<service-name>");
    }
    const attributePairs = missingFly
      .filter((entry) => entry.placeholder)
      .map((entry) => `${entry.feeds}=${entry.placeholder}`);
    if (attributePairs.length) {
      fixes.push(`    OTEL_RESOURCE_ATTRIBUTES=${attributePairs.join(",")}`);
    }
    console.warn(
      `[telemetry] Fly env not available (warned once, at registration). Fly sets these automatically at runtime; elsewhere provide them explicitly:\n${lines.join("\n")}\n  To set them, add to the environment (OTEL_RESOURCE_ATTRIBUTES is ONE variable, comma-separated — keep keys you already set and replace the <placeholders>):\n${fixes.join("\n")}`,
    );
  }

  const onFly = Boolean(fly.FLY_APP_NAME || fly.FLY_MACHINE_ID);
  const defaultAttributes = {
    ...(onFly ? { "cloud.provider": "fly_io" } : {}),
    "cloud.region": fly.FLY_REGION || "auto",
    "service.instance.id": fly.FLY_MACHINE_ID || "NA",
    "service.version": process.env.OTEL_SERVICE_VERSION || fly.FLY_IMAGE_REF || "NA",
    "deployment.environment.name": process.env.DEPLOYMENT_ENVIRONMENT || process.env.NODE_ENV || "development",
    // Legacy key the existing Grafana dashboards/queries filter on.
    ...(fly.FLY_REGION ? { "fly.region": fly.FLY_REGION } : {}),
  };
  const additions = Object.entries(defaultAttributes)
    .filter(([key]) => !providedKeys.has(key))
    .map(([key, value]) => `${key}=${value}`);
  if (additions.length) {
    process.env.OTEL_RESOURCE_ATTRIBUTES = [process.env.OTEL_RESOURCE_ATTRIBUTES, additions.join(",")]
      .filter(Boolean)
      .join(",");
  }

  const { diag, DiagConsoleLogger, DiagLogLevel } = require("@opentelemetry/api");
  const { NodeSDK } = require("@opentelemetry/sdk-node");
  const { getNodeAutoInstrumentations } = require("@opentelemetry/auto-instrumentations-node");
  const { OTLPTraceExporter } = require("@opentelemetry/exporter-trace-otlp-proto");
  const { resourceFromAttributes, envDetector, hostDetector, osDetector, processDetector } = require("@opentelemetry/resources");
  const { ATTR_SERVICE_NAME } = require("@opentelemetry/semantic-conventions");
  const { BatchSpanProcessor, ConsoleSpanExporter, ParentBasedSampler, SamplingDecision, TraceIdRatioBasedSampler } = require("@opentelemetry/sdk-trace-base");
  const { ScrubbingSpanProcessor } = require("./scrubbing-span-processor");

  // Export failures are non-fatal; surfacing them as log lines is the
  // difference between "traces are missing" and knowing why.
  const logLevel = (process.env.OTEL_LOG_LEVEL || "error").toUpperCase();
  diag.setLogger(new DiagConsoleLogger(), DiagLogLevel[logLevel] !== undefined ? DiagLogLevel[logLevel] : DiagLogLevel.ERROR);

  const serviceName = resolveServiceName();
  const ignored = ignoredPaths();

  // Everything else about the resource comes from the detectors (env, host,
  // os, process by default) and the OTEL_RESOURCE_ATTRIBUTES composed above;
  // only service.name is pinned explicitly so its fallback chain
  // (OTEL_SERVICE_NAME > FLY_APP_NAME > package.json name) always wins.
  const resource = resourceFromAttributes({
    [ATTR_SERVICE_NAME]: serviceName,
  });

  /**
   * Head sampling, parent-based: the entry service rolls the dice once per
   * request and every downstream service follows that decision, so sampled
   * requests keep their COMPLETE journey. Platform default is 0.1 — spans are
   * the code-level 10%; the 100% "which request went where and responded how"
   * record is the http-logger's job. Remember head sampling drops incident
   * traces at the same ratio; tail sampling is the deferred answer if that
   * ever hurts.
   */
  const ratio = Number(process.env.OTEL_TRACES_SAMPLER_ARG !== undefined ? process.env.OTEL_TRACES_SAMPLER_ARG : "0.1");
  const ratioSampler = new TraceIdRatioBasedSampler(Number.isFinite(ratio) ? ratio : 0.1);

  const rootSampler = {
    shouldSample(ctx, traceId, spanName, spanKind, attributes, links) {
      const target = attributes["url.path"] || attributes["http.target"] || attributes["http.route"];
      if (isIgnoredPath(target, ignored)) {
        return { decision: SamplingDecision.NOT_RECORD };
      }
      return ratioSampler.shouldSample(ctx, traceId, spanName, spanKind, attributes, links);
    },
    toString: () => `IgnorePaths(${ratioSampler.toString()})`,
  };

  const exporter = consoleMode
    ? new ConsoleSpanExporter()
    : // Deliberately constructed with no arguments: endpoint, headers and
      // protocol all come from the standard OTEL_EXPORTER_OTLP_* env vars,
      // which keeps retargeting a `fly secrets set` and credential handling
      // out of code entirely.
      new OTLPTraceExporter();

  // Resolved by name from OTEL_NODE_RESOURCE_DETECTORS (defaulted above) —
  // mapped explicitly rather than via auto-instrumentations-node, whose
  // helper export for this has changed names across versions.
  const DETECTORS = { env: envDetector, host: hostDetector, os: osDetector, process: processDetector };
  const resourceDetectors = process.env.OTEL_NODE_RESOURCE_DETECTORS.split(",")
    .map((name) => DETECTORS[name.trim()])
    .filter(Boolean);

  const sdk = new NodeSDK({
    resource,
    resourceDetectors,
    sampler: new ParentBasedSampler({ root: rootSampler }),
    spanProcessors: [new ScrubbingSpanProcessor(new BatchSpanProcessor(exporter))],
    spanLimits: {
      // Large enough for real SQL shapes and stack-trace events, bounded
      // enough to protect the storage byte budget.
      attributeValueLengthLimit: Number(process.env.OTEL_SPAN_ATTRIBUTE_VALUE_LENGTH_LIMIT || 2048),
      attributeCountLimit: 256,
      eventCountLimit: 256,
    },
    instrumentations: [
      getNodeAutoInstrumentations({
        // --- noise: high-frequency, near-zero diagnostic value ---
        "@opentelemetry/instrumentation-fs": { enabled: false },
        "@opentelemetry/instrumentation-dns": { enabled: false },
        "@opentelemetry/instrumentation-net": { enabled: false },
        "@opentelemetry/instrumentation-generic-pool": { enabled: false },
        "@opentelemetry/instrumentation-lru-memoizer": { enabled: false },
        "@opentelemetry/instrumentation-connect": { enabled: false },
        "@opentelemetry/instrumentation-router": { enabled: false },
        // --- not in this stack ---
        "@opentelemetry/instrumentation-aws-sdk": { enabled: false },
        "@opentelemetry/instrumentation-aws-lambda": { enabled: false },
        "@opentelemetry/instrumentation-grpc": { enabled: false },
        "@opentelemetry/instrumentation-kafkajs": { enabled: false },
        "@opentelemetry/instrumentation-mysql": { enabled: false },
        "@opentelemetry/instrumentation-mysql2": { enabled: false },
        "@opentelemetry/instrumentation-redis": { enabled: false },
        "@opentelemetry/instrumentation-koa": { enabled: false },
        "@opentelemetry/instrumentation-hapi": { enabled: false },
        "@opentelemetry/instrumentation-restify": { enabled: false },
        "@opentelemetry/instrumentation-tedious": { enabled: false },
        "@opentelemetry/instrumentation-cassandra-driver": { enabled: false },
        "@opentelemetry/instrumentation-oracledb": { enabled: false },
        "@opentelemetry/instrumentation-mongoose": { enabled: false },
        "@opentelemetry/instrumentation-memcached": { enabled: false },
        "@opentelemetry/instrumentation-dataloader": { enabled: false },
        "@opentelemetry/instrumentation-graphql": { enabled: false },
        "@opentelemetry/instrumentation-bunyan": { enabled: false },
        "@opentelemetry/instrumentation-undici": { enabled: false },
        "@opentelemetry/instrumentation-socket.io": { enabled: false },
        "@opentelemetry/instrumentation-amqplib": { enabled: false },

        "@opentelemetry/instrumentation-http": {
          // Belt and braces with the sampler — this drops the span before it
          // is ever created; the sampler drops it if something slips through.
          ignoreIncomingRequestHook: (request) => isIgnoredPath(request.url, ignored),
          // No header capture. Auth headers (usertoken, hashkey,
          // authorization) live there and must never reach the trace store.
        },

        "@opentelemetry/instrumentation-express": {
          // A span per middleware layer per request multiplies volume for no
          // signal. Router layers are kept — those are the route attribution.
          ignoreLayersType: ["middleware", "request_handler"],
        },

        "@opentelemetry/instrumentation-ioredis": {
          // Command and key only, never argument values — Redis values in
          // this stack include session payloads and tokens. Keys embed
          // internal pseudonymous ids, which is the attribution we want.
          dbStatementSerializer: (cmdName, cmdArgs) => (cmdArgs && cmdArgs.length ? `${cmdName} ${String(cmdArgs[0])}` : cmdName),
        },

        // enhancedDatabaseReporting stays OFF for both: that is the setting
        // that keeps bound parameter values out of db.statement. The
        // ScrubbingSpanProcessor is the backstop if a default ever changes.
        "@opentelemetry/instrumentation-pg": {
          enhancedDatabaseReporting: false,
          // pg-pool connect spans are the pool-exhaustion signal.
          requireParentSpan: false,
        },
        "@opentelemetry/instrumentation-mongodb": {
          enhancedDatabaseReporting: false,
        },

        // Injects trace_id/span_id into every winston/pino record logged
        // inside a request — the log<->trace pivot that makes VictoriaLogs
        // queries like `trace_id:abc` return every hop of one request. Both
        // stay on because the fleet is mixed (bo/core: winston; others pino —
        // the api-tester pilot lesson). Works only because this package loads
        // before the app's logger is built.
        "@opentelemetry/instrumentation-winston": { enabled: true },
        "@opentelemetry/instrumentation-pino": { enabled: true },
      }),
      // @prisma/instrumentation is DELIBERATELY NOT REGISTERED: its pinned
      // OTel 1.x span classes crash under this 2.x SDK the first time an
      // engine span is created (reproduced in softstudio-bo — see that repo's
      // tracing.ts for the full account). Revisit on a Prisma client upgrade.
    ],
  });

  sdk.start();
  console.log(`[telemetry] tracing enabled -> service=${serviceName} endpoint=${consoleMode ? "console" : endpoint} sampler=${ratio}`);

  // Fly sends SIGTERM on deploy and machine stop. Flush the last batch rather
  // than losing the spans for whatever was in flight — which, during a bad
  // deploy, is exactly the window worth having traces for.
  //
  // CRITICAL: registering a signal handler CANCELS the default termination,
  // so after flushing we must re-raise the signal or the process survives
  // kill forever (verified: pre-fix test processes outlived SIGTERM; on Fly
  // this was masked by the SIGKILL that follows the grace period). Re-raising
  // — rather than process.exit() — lets an app's own graceful-shutdown
  // handlers (Nest enableShutdownHooks etc.) keep working.
  let shuttingDown = false;
  const flush = (signal) => {
    if (shuttingDown) return;
    shuttingDown = true;
    Promise.race([sdk.shutdown(), new Promise((resolve) => setTimeout(resolve, 5000))])
      .catch((error) => console.error("[telemetry] shutdown error", error))
      .finally(() => {
        process.removeListener(signal, onSignal[signal]);
        // Let queued stdout drain (final log lines; console-exporter dumps)
        // before termination; 500ms backstop in case stdout never drains.
        const raise = () => process.kill(process.pid, signal);
        setTimeout(raise, 500).unref();
        process.stdout.write("", raise);
      });
  };
  const onSignal = {
    SIGTERM: () => flush("SIGTERM"),
    SIGINT: () => flush("SIGINT"),
  };
  process.on("SIGTERM", onSignal.SIGTERM);
  process.on("SIGINT", onSignal.SIGINT);
}

module.exports = { startTracing };
