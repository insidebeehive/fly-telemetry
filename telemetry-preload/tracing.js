"use strict";
/**
 * OpenTelemetry bootstrap — exports traces to the org telemetry app
 * (VictoriaTraces behind bhgrafana.flycast:10428).
 *
 * Ported from softstudio-bo apps/api/src/tracing.ts, which proved this tuning
 * in production against Grafana Cloud. Differences from that file:
 *   - service name defaults to FLY_APP_NAME (set by the platform, not devs)
 *   - default sample ratio is 0.1 (the platform decision: spans are the 10%
 *     code-level detail; the 100% request record lives in http-logger.js)
 *   - Sentry stays app-owned; this preload does not touch it
 *
 * DISABLED BY DEFAULT: with no OTEL_EXPORTER_OTLP_ENDPOINT configured — local
 * dev, tests, CI — nothing is constructed and the process behaves as before.
 *
 * NOTE for services being migrated: delete any in-repo OTel bootstrap
 * (tracing.ts / otel-instrument.ts) when this preload is enabled, or the
 * process will be double-instrumented.
 */

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
    console.log("[telemetry-preload] tracing disabled via OTEL_SDK_DISABLED");
    return;
  }

  const consoleMode = process.env.OTEL_TRACES_EXPORTER === "console";
  const endpoint = process.env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT || process.env.OTEL_EXPORTER_OTLP_ENDPOINT;

  // The endpoint is the master switch: no endpoint, no tracing, no SDK objects
  // constructed at all. This keeps local dev and test runs clean.
  if (!endpoint && !consoleMode) {
    console.log("[telemetry-preload] tracing disabled (no OTEL_EXPORTER_OTLP_ENDPOINT configured)");
    return;
  }

  // VictoriaTraces (and Grafana's gateway) speak protobuf over HTTP. Set
  // before the exporter is constructed so the standard env config applies.
  if (!process.env.OTEL_EXPORTER_OTLP_PROTOCOL) {
    process.env.OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf";
  }

  const { diag, DiagConsoleLogger, DiagLogLevel } = require("@opentelemetry/api");
  const { NodeSDK } = require("@opentelemetry/sdk-node");
  const { getNodeAutoInstrumentations } = require("@opentelemetry/auto-instrumentations-node");
  const { OTLPTraceExporter } = require("@opentelemetry/exporter-trace-otlp-proto");
  const { resourceFromAttributes } = require("@opentelemetry/resources");
  const { ATTR_SERVICE_NAME, ATTR_SERVICE_VERSION } = require("@opentelemetry/semantic-conventions");
  const { BatchSpanProcessor, ConsoleSpanExporter, ParentBasedSampler, SamplingDecision, TraceIdRatioBasedSampler } = require("@opentelemetry/sdk-trace-base");
  const { ScrubbingSpanProcessor } = require("./scrubbing-span-processor");

  // Export failures are non-fatal; surfacing them as log lines is the
  // difference between "traces are missing" and knowing why.
  const logLevel = (process.env.OTEL_LOG_LEVEL || "error").toUpperCase();
  diag.setLogger(new DiagConsoleLogger(), DiagLogLevel[logLevel] !== undefined ? DiagLogLevel[logLevel] : DiagLogLevel.ERROR);

  const serviceName = process.env.OTEL_SERVICE_NAME || process.env.FLY_APP_NAME || "unknown-service";
  const ignored = ignoredPaths();

  const resource = resourceFromAttributes({
    [ATTR_SERVICE_NAME]: serviceName,
    [ATTR_SERVICE_VERSION]: process.env.OTEL_SERVICE_VERSION || process.env.GIT_SHA || "unknown",
    // Literal keys rather than semconv constants: the incubating subpath is
    // not reliably resolvable under older TS/module configs, and these are
    // stable strings.
    "deployment.environment.name": process.env.DEPLOYMENT_ENVIRONMENT || process.env.NODE_ENV || "development",
    ...(process.env.FLY_REGION ? { "fly.region": process.env.FLY_REGION } : {}),
    ...(process.env.FLY_MACHINE_ID ? { "service.instance.id": process.env.FLY_MACHINE_ID } : {}),
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

  const sdk = new NodeSDK({
    resource,
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
        "@opentelemetry/instrumentation-pino": { enabled: false },
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

        // Injects trace_id/span_id into every winston record logged inside a
        // request — this is the log<->trace pivot that makes VictoriaLogs
        // queries like `trace_id:abc` return every hop of one request. Works
        // only because this preload runs before the app's logger is built.
        "@opentelemetry/instrumentation-winston": { enabled: true },
      }),
      // @prisma/instrumentation is DELIBERATELY NOT REGISTERED: its pinned
      // OTel 1.x span classes crash under this 2.x SDK the first time an
      // engine span is created (reproduced in softstudio-bo — see that repo's
      // tracing.ts for the full account). Revisit on a Prisma client upgrade.
    ],
  });

  sdk.start();
  console.log(`[telemetry-preload] tracing enabled -> service=${serviceName} endpoint=${consoleMode ? "console" : endpoint} sampler=${ratio}`);

  // Fly sends SIGTERM on deploy and machine stop. Flush the last batch rather
  // than losing the spans for whatever was in flight — which, during a bad
  // deploy, is exactly the window worth having traces for.
  let shuttingDown = false;
  const flush = () => {
    if (shuttingDown) return;
    shuttingDown = true;
    Promise.race([sdk.shutdown(), new Promise((resolve) => setTimeout(resolve, 5000))]).catch((error) => console.error("[telemetry-preload] shutdown error", error));
  };
  process.on("SIGTERM", flush);
  process.on("SIGINT", flush);
}

try {
  startTracing();
} catch (error) {
  console.error("[telemetry-preload] failed to initialise tracing, continuing without it", error);
}
